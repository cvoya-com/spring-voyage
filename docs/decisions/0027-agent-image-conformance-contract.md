# 0027 — Agent-image conformance contract (A2A 0.3.x on `:8999`, three conformance paths)

- **Status:** Accepted — 2026-04-22 — `A2AExecutionDispatcher` speaks A2A 0.3.x to every agent container on `${AgentLaunchSpec.A2APort}` (default `8999`). An image conforms by satisfying one of three paths: (1) `FROM ghcr.io/cvoya-com/agent-base:<semver>` and inherit the bundled bridge; (2) install the bridge via npm or copy the SEA binary into a custom base; (3) implement A2A 0.3.x natively. The bridge versioning is semver with N-2 backward compatibility on the dispatcher side; A2A is pinned to 0.3.x. Bind-mounted and co-launched sidecars are rejected.
- **Date:** 2026-04-22
- **Closes:** [#1087](https://github.com/cvoya-com/spring-voyage/issues/1087)
- **Related code:** `deployment/agent-sidecar/` (bridge source), `deployment/Dockerfile.agent-base`, `deployment/Dockerfile.agent.claude-code`, `deployment/Dockerfile.agent.dapr`, `.github/workflows/release-agent-base.yml`, `src/Cvoya.Spring.Dapr/Execution/A2AExecutionDispatcher.cs`, `src/Cvoya.Spring.Core/Execution/IAgentToolLauncher.cs` (`AgentLaunchSpec.A2APort`).
- **Related docs:** [`docs/architecture/agent-runtime.md` § 7 BYOI conformance contract](../architecture/agent-runtime.md#7-byoi-conformance-contract), [`docs/guide/byoi-agent-images.md`](../guide/operator/byoi-agent-images.md), [`deployment/agent-sidecar/README.md`](../../deployment/agent-sidecar/README.md), [ADR 0025 — Unified agent launch contract](0025-unified-agent-launch-contract.md), [ADR 0026 — Per-agent container scope](0026-per-agent-container-scope.md).

## Context

Once the dispatch path collapsed onto A2A (ADR 0025), the question of *what does an agent image have to look like* became a public contract instead of an internal implementation detail. Operators and the Cloud team both want to bring their own images: pre-baked CLI tools, custom system tooling, internal trust anchors, non-Debian distros, rootless / non-default-UID containers.

The constraint set is small enough to live in one document: a running container has to expose A2A 0.3.x on a known port, expose an Agent Card at a well-known URL, and honour the launcher-supplied environment. Everything else — the language the agent is written in, the base distro, the user the process runs as, whether the agent is a CLI wrapper or a native A2A server — is the operator's choice.

The harder question was *how to ship the bridge*. A bridge is required whenever the agent is a CLI tool (Claude Code, Codex, Gemini) — the dispatcher does not speak the tool's stdin/stdout protocol, the bridge does. Three shipping shapes were on the table:

1. **Bundle the bridge into a recommended base image.** Operators `FROM` it and add their tool. The bridge is the image's ENTRYPOINT.
2. **Distribute the bridge as an installable artifact.** npm package + standalone single-executable binary; operators install it into any base image they want.
3. **Bind-mount the bridge into every agent container at launch.** The dispatcher mounts the bridge from a host path into the container; the bridge is added to the container's PATH at launch.
4. **Co-launch a sidecar container.** Two containers per agent — the agent and a sidecar — sharing a network namespace. The sidecar runs the bridge.

Pressure points:

- **No host filesystem assumption.** The dispatcher already runs as a host process (#1063) and the agent containers run on the dispatcher's host. The bridge must be reachable inside the container without assuming a host-path layout — that breaks rootless deployments, breaks Kubernetes (no shared host filesystem), and ties the contract to a specific deployment topology.
- **No tight coupling between dispatcher version and agent image.** Operators must be able to update an agent image without redeploying the dispatcher, and vice versa. A bind-mounted bridge couples the two: the bridge in the container is the bridge on the dispatcher's host, so the dispatcher version is the bridge version.
- **One container per agent (ADR 0026).** A two-container topology (sidecar) doubles the container count, doubles the image-pull cost, and adds a network-namespace-sharing problem to every container runtime backend.

## Decision

**Define a small wire contract and ship the bridge through three operator-facing paths.**

### Wire contract

A container conforms when:

- It exposes A2A 0.3.x on `http://0.0.0.0:${AGENT_PORT}/`. `AGENT_PORT` defaults to `8999` (`AgentLaunchSpec.A2APort`); the dispatcher reads the port from the spec and dials the matching host port.
- It serves an Agent Card at `GET /.well-known/agent.json` whose `protocolVersion` is `"0.3"`. (`/.well-known/agent-card.json` is also accepted as an alias.)
- It implements the A2A JSON-RPC 2.0 verbs `message/send`, `tasks/cancel`, and `tasks/get` on `POST /`.
- Every response carries an `x-spring-voyage-bridge-version: <semver>` header (and a matching field on the Agent Card / task payload). The dispatcher logs version skew so operators can correlate odd behaviour with stale sidecars.
- It honours the launcher-supplied environment, including any `SPRING_*` keys the launcher stamped into `AgentLaunchSpec.EnvironmentVariables` (notably `SPRING_AGENT_ARGV`, `SPRING_MCP_ENDPOINT`, `SPRING_AGENT_TOKEN`, `SPRING_SYSTEM_PROMPT`).

That's the entire contract. There is no required base distro, no required user, no required tool layout.

### Three conformance paths

| Path | Recipe | When to pick it |
|------|--------|-----------------|
| **1** | `FROM ghcr.io/cvoya-com/agent-base:<semver>` and `RUN`-install your CLI tool. ENTRYPOINT is left as-is — the bundled bridge runs on `:8999` automatically. | Default. Fastest path. Works for anything that runs on Debian 12 + Node 22. |
| **2** | Pull the bridge into a custom base. Either `npm install -g @cvoya/spring-voyage-agent-sidecar` (Node-bearing image), or copy the static binary from each GitHub Release (`spring-voyage-agent-sidecar-{linux-amd64,linux-arm64,darwin-arm64}`) into a Node-less image. Set the binary as the `ENTRYPOINT`. | Non-Debian distro, rootless image with non-default UIDs, or you can't have Node in the runtime layer. |
| **3** | Implement A2A 0.3.x natively in your image. No bridge involved. | The image already speaks A2A natively (e.g. the Python Dapr Agent at `DaprAgentLauncher`). |

The Tier-A CLI launchers (Claude Code, Codex, Gemini) all use path 1 by default; the Dapr Agent launcher is the canonical example of path 3. Path 2 is exercised by the released SEA binaries' smoke step in `release-agent-base.yml`.

### Versioning commitment

- **Bridge.** Semver on the npm package (`@cvoya/spring-voyage-agent-sidecar`) and the OCI tag (`ghcr.io/cvoya-com/agent-base`). N-2 backward compatibility: a Spring Voyage worker dialing this bridge accepts versions within the last 2 majors. The bridge stamps its version on the `x-spring-voyage-bridge-version` header and the Agent Card so the dispatcher logs version skew.
- **A2A.** Pinned to `0.3.x`. A bump to `0.4.x` or `1.x` is a deliberate breaking change with a deprecation window on the dispatcher side; the protocol version on the Agent Card lets the dispatcher refuse mismatches early.
- **Release shape.** Bridge releases are cut on tags shaped `agent-base-vX.Y.Z`. The release workflow publishes the OCI image, the npm package, and the SEA binaries in lockstep so all three paths advance together.

## Alternatives considered

- **Bind-mount the bridge from the dispatcher's host into every agent container.** Avoids redistributing the bridge inside agent images. **Rejected.** Couples the dispatcher version and the bridge version, breaks rootless / non-shared-host topologies (Kubernetes), and forces every operator's container image to make assumptions about the host filesystem layout that the OSS contract explicitly avoids.
- **Co-launched sidecar container.** Two containers per agent, sharing a network namespace. **Rejected.** Doubles the per-agent container footprint (in conflict with ADR 0026's per-agent scope), doubles image-pull cost, and pushes a network-namespace-sharing problem onto every `IContainerRuntime` backend (`PodmanRuntime`, future `KubernetesPodRuntime`, …). The same goal — "the bridge isn't in your image" — is met by path 2 (npm or SEA binary), which keeps the agent in one container.
- **Require A2A natively from every image (no bridge).** Drop the bridge entirely; require all agents to speak A2A directly. **Rejected.** Forces every operator wrapping a CLI tool (Claude Code, Codex, Gemini, plus any custom CLI a Cloud customer brings) to hand-roll a JSON-RPC server and a process spawner. The bridge is small (~600 LOC), maintained centrally, and version-pinned; reinventing it per tool is strictly more code.
- **Bundle the bridge as a sidecar in the launcher (CLI flag).** A `spring agent run --bridge` flag that wraps the agent. **Rejected.** Conflates the CLI surface with the container contract. The agent runs in a container the operator controls; the CLI runs on the operator's workstation. They should not share an entry point.

## Consequences

### Gains

- **Three real paths, not one.** Operators choose by image constraint (distro, UID model, Node-availability), not by lock-in. A Cloud customer with a hardened RHEL base can pick path 2 (SEA binary) without forking the recommended path.
- **The wire contract is one screen.** A2A 0.3.x on `:8999`, Agent Card at `/.well-known/agent.json`, version header on every response. New custom images conform by matching the contract; the platform doesn't care how they got there.
- **Version skew is observable.** The bridge stamps its version on every response; the dispatcher logs the skew. A stale sidecar in an operator's custom image surfaces as a log line, not as a 4 a.m. mystery.
- **Bridge releases are an OSS surface.** The release workflow ships all three paths together (OCI image, npm package, SEA binaries) so every conformance path advances at the same cadence.

### Costs

- **The bridge is a real piece of code we maintain.** ~600 LOC of TypeScript with its own test suite, npm package, OCI image, and SEA binaries across three architectures. The cost is bounded — the contract is small — but it's not zero.
- **N-2 backward compatibility on the bridge is a real commitment.** A bridge change has to remain dispatcher-compatible across two majors. The dispatcher logs skew but does not refuse to talk; an N-3 bridge in a customer's image becomes a support conversation, not an automatic failure.
- **A2A 0.3.x → 0.4.x is a coordinated change.** Bumping the A2A version on the dispatcher requires the bridge to bump too, the Dapr Agent (path 3) to update its server, and operators on path 2 to take the bridge update. Mitigated by the deprecation window and by the `protocolVersion` field surfacing the mismatch early; not eliminated.

### Known follow-ups

- **Path-2 (npm-installed bridge) reference image** — a Dockerfile under `deployment/examples/dockerfiles/` that exercises the npm install path end-to-end, plus a CI smoke. Filed as a follow-up so the OSS sample set is complete.
- **Bridge upgrade automation** — when an operator's custom image pins a bridge version, surface "your bridge is N versions behind" in the agent's status payload (not just dispatcher logs).

## Revisit criteria

Revisit if any of the below hold:

- A2A 0.4.x or 1.x ships and the ecosystem moves quickly enough that pinning to 0.3.x becomes a liability. At that point bump the contract, ship a coordinated release across all three paths, and amend this ADR with the deprecation window.
- A meaningful fraction of operators (Cloud customer feedback, OSS issue volume) cannot satisfy paths 1–3 because of a constraint we didn't anticipate (e.g. a runtime that bans both Node and a foreign binary). That's the trigger to add a path 4 — most likely a small Go or Rust port of the bridge — rather than relax the wire contract.
- The bridge's N-2 compatibility window starts costing more than it buys (e.g. each bridge release ships back-compat shims for two majors and the shim layer becomes the bug source). At that point shrink to N-1 and amend this ADR.
