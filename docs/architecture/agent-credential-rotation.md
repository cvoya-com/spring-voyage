# Agent Credential Rotation — Design Rationale

> **[Architecture Index](README.md)** | **Design output for the agent-runtime-boundary § 2.2.3 contract.** Owned by Area D. Pointers: [`docs/specs/agent-runtime-boundary.md` § 2.2.3](../specs/agent-runtime-boundary.md#223-credential-rotation), [ADR-0029](../decisions/0029-tenant-execution-boundary.md), [#1325](https://github.com/cvoya-com/spring-voyage/issues/1325) (the forcing issue), [`src/Cvoya.Spring.Dapr/Actors/ContainerSupervisorActor.cs`](../../src/Cvoya.Spring.Dapr/Actors/ContainerSupervisorActor.cs) (the supervisor whose `RestartAsync` this design fixes), [`src/Cvoya.Spring.Core/Execution/IAgentContextBuilder.cs`](../../src/Cvoya.Spring.Core/Execution/IAgentContextBuilder.cs) (the seam the design extends).

---

## Framing

`IAgentContext` (D1 spec § 2) hands every agent container a bundle of scoped credentials at first launch — a Bucket-2 token, an LLM-provider token, an MCP token, optionally a telemetry token. They are minted per launch by `IAgentContextBuilder`, agent-scoped, never reused across agents, and delivered through env vars (`SPRING_BUCKET2_TOKEN`, `SPRING_LLM_PROVIDER_TOKEN`, `SPRING_MCP_TOKEN`, `SPRING_TELEMETRY_TOKEN`) plus mounted files under `/spring/context/` for structured payloads.

D3a shipped the per-launch path. D3d shipped a per-agent supervisor (`ContainerSupervisorActor`) that restarts crashed containers from persisted image + volume state. The intersection of those two is the bug #1325 names: the supervisor's `RestartAsync` re-launches with persisted image and volume but does **not** re-inject env-var-borne credentials. A restarted container therefore comes up with empty `SPRING_*_TOKEN` values and fails at `initialize()`.

The spec section that should answer "how do credentials reach a restarted container" — § 2.2.3 — said only "TBD in Stage 2." This document fills that gap by picking among three options enumerated in #1325 and fixing the answer in the spec.

The two questions this design has to answer separately:

1. **Restart-time credential refresh** (the #1325 bug): how does a supervisor restart get fresh credentials into the new container?
2. **Mid-execution credential rotation** (the broader § 2.2.3 surface): how does the platform rotate a credential on a long-lived container without forcing a restart?

These look like the same question but they are not. Question 1 needs a mechanism today; question 2 is "could be useful later." The design lands on a single primitive that addresses (1) cleanly today and leaves (2) as an additive future evolution.

---

## The three options

#1325 enumerates them. Restated here for the rationale:

### Option 1 — Persist env vars in `SupervisorState`

The supervisor caches the env-var bundle from the original `StartAsync` call into its Dapr state and replays it on restart.

**Pro.** One-line fix. No new methods, no new components.

**Con.** The cure is worse than the bug. `SupervisorState` is a Dapr actor state document — it is the wrong place to hold long-lived credential tokens. Every supervisor instance becomes a secret-storage liability, the blast radius of a state-store compromise widens to every running agent's tokens, and the credentials sit at rest in a store that was never designed for that posture. This option is also misleading: it papers over expiry rather than fixing the rotation story — the cached tokens from launch N would themselves expire eventually.

**Verdict: rejected**, consistent with #1325's framing.

### Option 2 — Re-call the platform-side context builder on restart

The supervisor calls back into `IAgentContextBuilder` (the same seam D3a defined for first launch) to mint a fresh credential set, then launches the container with those credentials.

**Pro.**
- Uses the seam that already exists. No new component, no sidecar, no in-container refresher.
- Credentials are minted exactly once per launch (the same posture D3a established for first launch).
- The "network hop per restart" framing in #1325's option list is a red herring for OSS: in Spring Voyage's deployment, the supervisor and the context builder both live in the same worker process. The call is an in-process DI invocation, not a network hop. The cloud overlay can swap the `IAgentContextBuilder` implementation per `AGENTS.md` § "Open-source platform and extensibility" without changing the contract.
- The fresh credentials are visible **only** to the relaunched container's PID 1, never persisted by the supervisor, never sit at rest in actor state.
- The Python SDK doesn't change. The env-var contract doesn't change. The D3a code path doesn't change. The only contract change is one new method on `IAgentContextBuilder` (or equivalent — the supervisor needs to be able to ask for a refresh given an agent identity).

**Con.**
- The supervisor needs to know enough about the original launch to ask for the right credential set — minimally the agent identity, possibly more (tenant id, unit id, agent-definition handle). Some of this is already on `SupervisorState` (`AgentId`); some has to be added.
- The supervisor and the context-build pipeline now have a runtime coupling. That coupling is small (one method call) but it didn't exist before D3d. This is the cost the issue's option list flagged.

### Option 3 — Mounted files + credential refresher (per § 2.2.3 TBD direction)

Credentials live in files under `/spring/context/credentials/`, written by a refresher process (a sidecar in the agent's container, a host-side daemon writing through a shared mount, or the platform-side `IAgentContextBuilder` writing through a known mount path). The supervisor never touches credentials — the refresher writes fresh tokens whenever the platform rotates them, and the SDK reads them off disk lazily on each platform call (or re-reads on a refresh signal).

**Pro.** The cleanest long-term shape for **mid-execution rotation**. A long-running persistent agent can keep running across rotation events; no restart needed. The supervisor stays single-purpose (lifecycle), and credential rotation becomes a separate concern owned by a separate component. Aligns with the existing § 2.2.3 framing.

**Con.**
- Requires a refresher process. In OSS this is not free: a sidecar adds a container per agent, breaking the per-agent-container scope ADR-0026 establishes; a host-side writer needs write access to a volume the tenant container also reads, which has its own isolation implications; and a platform-side writer that pokes into `/spring/context/` needs container-runtime support for live mount mutation (Podman supports this poorly and Kubernetes' `Secret`-as-volume update has its own latency profile).
- The Python SDK has to change. Today the SDK reads `SPRING_BUCKET2_TOKEN` etc. from env vars at `initialize` and caches the values for the container lifetime. To consume mounted-file credentials, the SDK would have to: (a) prefer a file-channel value when present; (b) re-read on every credentialed call (or on a refresh signal); (c) handle the race where the file is being rewritten as the SDK reads it. This is a non-trivial SDK redesign.
- The D1 spec § 2.2.1 env-var contract becomes ambiguous (env value? file value? which wins?) unless the spec is amended to say env vars are first-launch only and file values supersede on rotation — which is a real contract change downstream consumers would have to adopt.
- Solves a problem we don't have today. v0.1 has no requirement for zero-downtime rotation. Restart is acceptable as the rotation primitive at this stage.

**Verdict on the rotation strategy:** Option 3 is the right shape **eventually**, but adopting it today to fix #1325 is overcorrection. The bug #1325 names is solved more cleanly by Option 2, and Option 3 is then available as an additive future evolution when a forcing case appears (e.g., a tenant whose agent uptime SLA can't tolerate restart-driven rotation).

---

## Decision

**Restart is the rotation primitive.** Option 2 fills § 2.2.3 today. Option 3 is recorded as an additive future evolution, not deferred indefinitely but not on the critical path for v0.1.

The contract that lands in § 2.2.3:

- The platform mints fresh, agent-scoped credentials on **every** container launch — first launch and every supervisor-driven restart.
- The supervisor MUST route restarts through the same `IAgentContext` build path used for first launch. It MUST NOT cache the previous launch's credentials.
- New credentials reach a container only via a new launch. The platform MUST NOT mutate env vars or mounted files of a running container.
- The SDK MUST re-read credentials at the top of every `initialize` and MUST treat auth failures from platform-provided endpoints as fatal in-flight errors (no silent retry).
- Operators size token TTLs to comfortably exceed the agent's idle-eviction window — a healthy container never observes an expiry mid-run; rotation is enacted by the next restart cycle.

This makes restart the platform-controlled lever for credential rotation. A platform-driven proactive rotation is a stop-and-restart issued by the supervisor (or a higher-level component asking the supervisor to cycle); credential revocation is also a stop-and-restart.

---

## Why Option 2 wins for v0.1

- **Smallest contract change.** One new method on `IAgentContextBuilder` (call it `RefreshCredentialsForRestartAsync(agentId, …)`, exact name TBD by the implementation PR). The Python SDK is unchanged. The D3a env-var path is unchanged. The D1 § 2.2.1 env-var canon stays as the single credential channel.
- **Reuses an existing seam.** D3a already defined `IAgentContextBuilder` precisely so platform-side credential minting could be swapped per deployment (cloud KMS-backed in the cloud overlay; cryptographic-random in OSS). The supervisor restart path slots into that seam.
- **No new component, no new container.** No refresher sidecar (avoids the ADR-0026 per-agent-container-scope tension), no host-side daemon (avoids the volume-isolation tension), no live mount mutation (avoids relying on container-runtime features that aren't uniform across Podman and K8s).
- **Credentials don't sit at rest in supervisor state.** The supervisor holds enough launch metadata (agent id, tenant id, etc.) to call back into the builder, but never holds tokens.
- **Bounds the rotation latency to one restart cycle.** A restart is ≪ 30s in OSS (Podman start + agent `initialize`); for v0.1 that is fast enough that operators can plan TTL + restart cadence around it without exotic scheduling.

The cost is the supervisor-to-context-builder coupling — a runtime dependency that didn't exist before D3d. That cost is small (one DI-injected interface, one new method) and load-bearing: the supervisor already crosses the platform-side boundary to talk to the container runtime; talking to the credential builder on the same boundary is consistent.

---

## What changes

### Spec (`docs/specs/agent-runtime-boundary.md` § 2.2.3)

Replaces "TBD in Stage 2" with the normative requirements above. Conformance items added in § 2.3 and § 5. Out-of-scope updated to flag long-running zero-downtime rotation as an additive future evolution (pointing back to this doc).

### Code (follow-up implementation issue — [#1347](https://github.com/cvoya-com/spring-voyage/issues/1347))

Three concrete changes belong in the implementation PR:

1. **`IAgentContextBuilder` gains a refresh entry point.** Exact shape TBD by the implementation PR; the constraint is that the supervisor can hand it the minimum identity needed (agent id, tenant id, possibly unit id) and get back a fresh `AgentBootstrapContext` (the env-var dictionary + the file dictionary). The default `AgentContextBuilder` implementation reuses the existing `BuildAsync` body — fresh tokens are already minted there per call. The cloud overlay's tenant-scoped variant rebuilds against KMS-backed signing on the same seam.
2. **`ContainerSupervisorActor.RestartAsync` calls the refresh entry point.** The supervisor already persists `AgentId`, `Image`, `NetworkName`, `VolumeName`. It needs to capture (or re-derive) enough launch metadata to invoke the builder — at minimum the tenant id and unit id. The launch metadata MUST NOT include credential material; only identity. The fresh `AgentBootstrapContext` is merged into the `ContainerConfig` exactly the way `StartAsync` does today via `BuildEnvironmentVariables`.
3. **Tests.** The supervisor restart path gains coverage for "credentials are present and distinct from the previous launch's after `RestartAsync`." The `IAgentContextBuilder` test surface gains coverage for the refresh entry point producing a different token set on each call.

The Python SDK, the `agents/dapr-agent/agent.py` reference agent, the D1 § 2.2.1 env-var contract, and the D3a tests are **untouched**.

### State surface

`SupervisorState` gains whatever launch-identity fields the supervisor needs to re-call the builder (concretely: `TenantId` and optionally `UnitId`, both of which are non-secret). It MUST NOT gain any token field.

---

## Failure modes (and how the design handles them)

### Restart re-mint fails

The platform-side `IAgentContextBuilder` could fail mid-restart — the cloud KMS is unreachable, the database that holds tenant config is down, an upstream secret store throws. The supervisor's existing failure handling treats restart failure as transient: it logs, leaves status at `CrashDetected`, and the next health-check reminder retries. That posture extends naturally to credential-mint failures — a transient KMS outage produces the same "restart deferred to next poll" loop as a transient container-runtime outage, with the same `MaxRestarts` cap.

### Restart re-mint succeeds but the new credentials are revoked between mint and container start

The container will come up, attempt its first credentialed call, and observe an auth failure. Per the spec § 2.2.3 contract, the SDK MUST surface the failure as a fatal in-flight error — non-zero exit. The supervisor's health check observes the dead container and triggers another restart. The next mint should pick up the revocation (assuming revocation is propagated to the credential issuer; that's a separate design — see "out of scope" below). Worst case is `MaxRestarts` cycles before the supervisor gives up, which matches today's behaviour for any persistent-failure mode.

### A long-running container's credentials expire mid-run

By § 2.2.3, the platform MUST NOT issue tokens whose TTL is shorter than the container's expected uptime. Operators sizing TTLs correctly avoids this case. If it occurs anyway (operator misconfiguration, clock skew), the SDK's first auth-failure response (per § 2.2.3) takes the container down, and the supervisor restarts it with fresh credentials. This is degraded but correct.

### Rotation-while-handling-message race

A request to rotate credentials arrives while `on_message` is in flight. By the contract, rotation = stop + restart. The stop is signalled by SIGTERM, which triggers `on_shutdown` per § 1.3 — the in-flight `on_message` is cancelled cooperatively within the grace window, the container exits, the supervisor restarts with fresh credentials, and the platform re-delivers the unacked message (Bucket 2 retry) to the new container instance. This is exactly the `platform_restart` shutdown reason the spec already defines.

---

## What's out of scope for this design

Each of these is its own follow-up; this design intentionally does not pull them in.

- **Long-running zero-downtime credential rotation.** Option 3's mounted-files + refresher mechanism. Documented above as the future evolution shape; deferred until a forcing case appears (e.g., a tenant whose SLA prohibits restart-driven rotation). When the case appears, the path is additive: a new path under `/spring/context/credentials/` writes structured credential files; the env-var channel of § 2.2.1 stays as the canonical first-launch surface; SDKs that consume only env vars remain conforming.
- **Platform credentials API backed by a secrets store.** A sibling to Option 3 for the same long-running zero-downtime rotation problem: instead of writing credential files into a mounted path, the platform exposes a Web API (e.g., `GET /agent/credentials`) backed by a managed secrets store, and the SDK fetches credentials at `initialize` and re-fetches on auth failure or on a refresh signal. Operationally cleaner than file-mounting (no live mount mutation, no sidecar, central audit + at-rest encryption come for free) and maps naturally onto the cloud overlay's existing identity story. Carries the same fundamental constraint as any "fetch on demand" shape: the API call must authenticate, so the container still needs a launch-time bootstrap credential — workload identity (IAM-for-Tasks, K8s ServiceAccount tokens, GCP Workload Identity) on managed clouds; an env-var bootstrap token on OSS Podman, where no native workload identity exists. The bootstrap token's rotation is then the same restart-driven primitive this design adopts for v0.1, so OSS doesn't materially benefit from layering the API in. Cloud overlays may swap their `IAgentContextBuilder` to mint a short-lived bootstrap token and stand up the credentials API independently — the contract in § 2.2.3 is unchanged because the SDK still receives credentials via the env-var channel at first launch; what changes is what those env-var values *are* (long-lived tokens vs. short-lived bootstrap tokens) and where the SDK looks for refreshed values (env vars only vs. env vars + API). Not on the v0.1 critical path; flagged here so the future-evolution conversation has both shapes in view.
- **Multi-tenant credential isolation.** Already handled by per-launch scoping (D3a) and the Bucket-2 auth model (§ 4.5). Not affected by this design.
- **Credential-revocation propagation.** When the platform revokes a credential, how does that signal reach the credential issuer in time for the next mint to produce a different token? This is a real concern (especially in the cloud overlay where a KMS revocation has its own propagation latency), but it is its own design — the supervisor restart path described here is correct **given** that revocation has propagated to the issuer; the issuer-side propagation contract is separate.
- **Platform-side credential storage hardening.** How tokens are stored at rest in the platform's own data plane (logs, metrics, audit records). Outside this design's scope.
- **Token-format observability.** Whether tokens are opaque, JWT, signed, etc. The spec already says tokens SHOULD be opaque to the agent (§ 4.5). Internal format choices are deployment-specific.

---

## Compatibility with what's shipped

| Surface | Change? |
|---|---|
| D1 spec § 2.2.1 env-var canon | No change. Env vars remain the credential-delivery channel. |
| D1 spec § 2.2.2 mounted-file canon | No change. No new files under `/spring/context/`. |
| D3a `IAgentContextBuilder.BuildAsync` | No signature change. New method added (refresh entry point). |
| D3a `AgentContextBuilder` (default impl) | New method delegates to existing builder body. |
| Python SDK `IAgentContext.load()` | No change. Reads env vars at `initialize` exactly as today. |
| `agents/dapr-agent/agent.py` | No change. |
| `ContainerSupervisorActor.RestartAsync` | Changed: calls the refresh entry point and injects fresh env vars. |
| `SupervisorState` | Additive: gains tenant id (and optionally unit id) to support the refresh call. No token field. |
| Existing D3a / D3d tests | No expected breakage. New tests added per "Tests" above. |

The cloud overlay's `AgentContextBuilder` implementation (which mints KMS-signed tokens) gets the new method without changing its existing semantics — refresh becomes a sibling of build, sharing the same KMS path.

---

## Open questions (flagged for the implementation issue, not blocking the design)

- **Refresh entry-point signature.** Whether the new method takes `agentId` plus a small launch-context record, or a full `AgentLaunchContext` reconstructed from `SupervisorState` plus the agent registry. The constraint is that nothing in the request payload can be a token; identity only. The implementation PR settles the exact shape against the smallest workable surface.
- **`SupervisorState` migration.** Adding fields to the persisted state record is a Dapr-state migration concern. The implementation PR has to handle existing supervisors with the old state shape (default the new fields to safe values; first restart reads them from a fallback path).
- **Telemetry on restart credential mint.** Worth instrumenting (count of restart re-mints per agent, mint latency, mint failures) but exact metric names are an implementation choice.
