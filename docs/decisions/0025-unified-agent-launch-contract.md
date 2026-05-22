# 0025 — Unified agent launch contract (single dispatch path, response-capture as a property)

- **Status:** Accepted — 2026-04-22 — `A2AExecutionDispatcher` runs every dispatch (ephemeral and persistent) through a single `StartAsync → readiness probe → A2A roundtrip → conditional teardown` pipeline. Launchers return one record (`AgentLaunchSpec`) describing the workspace, env, argv, stdin payload, A2A port, and response-capture mechanism. The legacy `AgentLaunchPrep` shape and the ephemeral "stdout-as-message" branch are gone.
- **Date:** 2026-04-22
- **Closes:** [#1087](https://github.com/cvoya-com/spring-voyage/issues/1087)
- **Related code:** `src/Cvoya.Spring.Core/Execution/IAgentToolLauncher.cs` (`AgentLaunchSpec`, `AgentResponseCapture`), `src/Cvoya.Spring.Dapr/Execution/A2AExecutionDispatcher.cs`, `src/Cvoya.Spring.Dapr/Execution/EphemeralAgentRegistry.cs`, `src/Cvoya.Spring.Dapr/Execution/PersistentAgentRegistry.cs`, `src/Cvoya.Spring.Dapr/Execution/ContainerConfigBuilder.cs`, `src/Cvoya.Spring.Dapr/Execution/{ClaudeCodeLauncher,CodexLauncher,GeminiLauncher,SpringVoyageAgentLauncher}.cs`.
- **Related docs:** [`docs/architecture/agent-runtime.md`](../architecture/agent-runtime.md), [`docs/architecture/workflows.md`](../architecture/agent-runtime.md), [ADR 0017 — A Unit IS an Agent](0017-unit-is-an-agent-composite.md), [ADR 0011 — Persistent-agent lifecycle HTTP surface](0011-persistent-agent-lifecycle-http-surface.md), [ADR 0019 — Domain workflows run as containers](0019-workflow-as-container.md), [ADR 0026 — Per-agent container scope](0026-per-agent-container-scope.md), [ADR 0027 — Agent-image conformance contract](0027-agent-image-conformance-contract.md).

## Context

Through the early waves of the dispatcher (#334 / #390 / #396), the platform shipped two structurally different paths to invoke an agent tool:

1. **Persistent path.** `A2AExecutionDispatcher` ensured a long-lived container was running (`PersistentAgentRegistry`), waited on `GET /.well-known/agent.json`, then sent an A2A `message/send`. The container's PID 1 was the tool's own A2A server (Dapr Agent) or a wrapper bridge.
2. **Ephemeral path.** The dispatcher ran a one-shot container (`podman run --rm` against `sleep infinity`-style entrypoints) and harvested the response either by reading stdout after the container exited or by reading a well-known file from the bind-mounted workspace.

The two paths drifted enough to be a bug magnet. Three problems forced a unification:

1. **Two contracts for "what does a launcher return?"** Persistent launchers returned an `AgentLaunchPrep` describing a long-lived container; ephemeral launchers returned a near-identical shape but the dispatcher interpreted it differently (foreground vs detached, argv vs entrypoint, stdout vs A2A). New launchers had to pick a tier and could not move between them. Hosting (`Ephemeral`/`Persistent`) became a contract switch instead of a runtime policy.
2. **Stdout as the protocol.** Ephemeral dispatch read the agent's response from stdout. That tied the wire format to whatever the tool printed (often interleaved with progress noise), forced the dispatcher to know per-tool output shapes, and made cancellation undefined — there was no way to ask the tool "stop now" mid-run without `SIGKILL`.
3. **Volume-drop never worked end-to-end.** The third option that surfaced during the design discussion ("write the response to `${WORKSPACE}/response.json` and read it after exit") survived as design notes but was never wired through: it left the cancellation problem open, doubled the on-disk surface for credential-leak audits, and could not be retrofitted onto launchers that already spoke A2A natively.

By the time PR 4 of [#1087](https://github.com/cvoya-com/spring-voyage/issues/1087) landed, every launcher we shipped (Claude Code, Codex, Gemini, Dapr Agent) was speaking A2A in some form. The duplication had no remaining justification — only inertia.

## Decision

**Adopt a single launcher contract, `AgentLaunchSpec`, and a single dispatch path. Hosting becomes a retention policy on the same lifecycle, not a path switch. Response capture is a property of the spec, defaulting to A2A.**

### One contract

`IAgentToolLauncher.PrepareAsync(AgentLaunchContext) → AgentLaunchSpec`. The spec is pure data:

| Field                  | Purpose                                                                                                |
| ---------------------- | ------------------------------------------------------------------------------------------------------ |
| `WorkspaceFiles`       | Per-invocation files (relative path → text content). The dispatcher materialises and bind-mounts them. |
| `EnvironmentVariables` | Stamped into the container.                                                                            |
| `WorkspaceMountPath`   | Where to bind-mount the workspace inside the container.                                                |
| `ExtraVolumeMounts`    | Additional mounts (rare).                                                                              |
| `WorkingDirectory`     | Optional `WORKDIR` override.                                                                           |
| `Argv`                 | Optional argv vector. Empty means "use the image ENTRYPOINT" (the agent-base bridge or a native A2A server). |
| `User`                 | Optional uid[:gid] / username override.                                                                |
| `StdinPayload`         | Optional UTF-8 payload the bridge feeds to the spawned tool's stdin (used by Claude Code).            |
| `A2APort`              | TCP port the in-container A2A endpoint listens on (default `8999`).                                    |
| `ResponseCapture`      | `A2A` (default) / `Stdout` / `VolumeDrop`. Only `A2A` is wired today; the other values are reserved.   |

`AgentLaunchPrep` is gone. Every launcher returns `AgentLaunchSpec` and the dispatcher never branches on launcher tier.

### One dispatch path

```
A2AExecutionDispatcher.DispatchAsync(message, context)
  → IAgentDefinitionProvider.GetByIdAsync(agentId)
  → IPromptAssembler.AssembleAsync(message, context)
  → IMcpServer.IssueSession(agentId, conversationId)
  → launcher.PrepareAsync(launchContext)             // returns AgentLaunchSpec
  → ContainerConfigBuilder.Build(image, spec)        // single seam to ContainerConfig
  → IContainerRuntime.StartAsync (detached)          // ephemeral OR persistent: same call
  → poll GET /.well-known/agent.json on :A2APort     // readiness probe
  → A2AClient.SendMessageAsync(SendMessageRequest)   // A2A roundtrip, both modes
  → MapA2AResponseToMessage(...)                     // A2A response → Spring message
  → ephemeral: EphemeralAgentRegistry.ReleaseAsync   // teardown on turn drain
    persistent: leave running, registered in PersistentAgentRegistry
```

The only branch is the post-roundtrip lifecycle decision: ephemeral tears the container down through `EphemeralAgentRegistry`; persistent leaves it registered for the next turn. Both registries call the same `IContainerRuntime` seam (`StartAsync` / `StopAsync`); the dispatcher does not know which one is in play until after the A2A response has been mapped.

### Hosting is a retention policy

`AgentHostingMode.Ephemeral` and `AgentHostingMode.Persistent` become orthogonal to dispatch shape. They control:

- whether the dispatcher releases the container after the turn drains (`Ephemeral`) or leaves it registered for reuse (`Persistent`);
- whether the registry health monitor restarts the container on liveness failure (`Persistent` only);
- whether `spring agent deploy` / `undeploy` (ADR 0011) can target the agent (`Persistent` only).

Both modes use the **same** PID-1 inside the container — either the agent-base bridge (BYOI conformance path 1 / 2) or a native A2A server (path 3). The container never runs `sleep infinity` waiting for the dispatcher to exec into it.

### Response capture is a property

`AgentResponseCapture.A2A` is the default and the only value implemented today. `Stdout` and `VolumeDrop` are reserved on the enum so a future launcher can opt into a different capture mechanism without bumping the launcher contract again. New entries land with their own ADR amendment.

## Alternatives considered

- **Stdout-as-message (status quo, ephemeral side).** The dispatcher reads stdout after the container exits and treats it as the response. **Rejected.** Tied the wire format to whatever the tool printed (with no separation between progress, errors, and the final answer), made cancellation undefined (no `tasks/cancel` verb on stdout), and prevented streaming. The Tier-A CLIs (Claude Code, Codex, Gemini) all needed a wrapper anyway to emit structured output, so wrapping them in an A2A bridge was strictly less code than wrapping them in a stdout-protocol normaliser.
- **Volume-drop as a response channel.** The dispatcher reads `${WORKSPACE}/response.json` after the container exits. **Rejected.** Doubles the on-disk credential-leak surface (the response file must be redacted with the same care as the workspace), still leaves cancellation undefined, and is impossible to retrofit onto containers that already speak A2A. Reserved on the `AgentResponseCapture` enum so a future launcher with a strict offline use case (no A2A, no stdout) can opt in with a follow-up ADR.
- **Two contracts, one dispatcher.** Keep `AgentLaunchPrep` for ephemeral and `AgentLaunchSpec` for persistent; collapse only the dispatcher path. **Rejected.** The dispatcher's branch on hosting was the smaller half of the duplication; the launcher-side branch was the painful half. Keeping both contracts would force every new launcher to choose a tier at design time and re-design if the agent's hosting mode changed.
- **A separate "ephemeral dispatcher" service.** Extract ephemeral dispatch into its own service, like `spring-dispatcher` (ADR 0012). **Rejected.** The seam is `IContainerRuntime`, which already routes through `spring-dispatcher`. The duplication was at the launcher and dispatcher layers, not at the container-runtime layer. A second service would add an HTTP hop without solving the contract drift.

## Consequences

### Gains

- **One mental model.** A new launcher fills in `AgentLaunchSpec` and stops. The dispatcher does not care whether the launcher is wrapping a CLI or invoking a native A2A server.
- **Cancellation is well-defined.** A2A `tasks/cancel` propagates through the bridge to `SIGTERM` on the spawned tool process (the bridge waits `AGENT_CANCEL_GRACE_MS` before `SIGKILL`).
- **Persistent and ephemeral are testable in the same harness.** The integration smoke (`tests/integration/Cvoya.Spring.Integration.Tests/EphemeralDispatchSmokeTests.cs`) exercises the same `StartAsync → ReleaseAsync` round-trip the persistent path uses; only the registry differs.
- **BYOI is a real contract.** Operators bringing their own image satisfy a single, written wire contract (ADR 0027) and slot into the same dispatch path the OSS launchers use.
- **Pooled hosting is no longer a special-case shape.** Adding `AgentHostingMode.Pooled` (#362) becomes a registry change — `EphemeralAgentRegistry` becomes a pool; the dispatcher path does not move.

### Costs

- **Every container must speak A2A.** Tools that don't speak A2A natively need the agent-base bridge. The bridge is small (~600 LOC of TypeScript), distributed three ways (FROM the base image, npm-installed, single-executable binary), and version-pinned (N-2 backward compatibility). The cost is bounded; the alternative — a per-tool stdout/volume-drop driver — was strictly more code.
- **Readiness probe on every dispatch.** Ephemeral dispatch now does a `GET /.well-known/agent.json` round-trip (60s budget, 200ms backoff) before sending the message. The cost is tens of milliseconds against a warm bridge; the gain is "no first-message race". Cold images still pay image-pull time; that's unchanged.
- **`AgentResponseCapture.Stdout` and `.VolumeDrop` are dead enum values today.** Reserved-on-enum-but-unwired surfaces drift. Mitigated by an explicit `NotSupportedException` in the dispatcher when a launcher returns one of them, plus a unit test that asserts the throw.

### Known follow-ups

- **[#362](https://github.com/cvoya-com/spring-voyage/issues/362)** — implement `AgentHostingMode.Pooled`. The launcher contract is already pooled-friendly (the spec is pure data; the registry can pre-warm against it).
- **[#539](https://github.com/cvoya-com/spring-voyage/issues/539)** — A2A message gateway. Will register an `ISkillInvoker` that translates an inbound A2A request to a Spring `Message`; the launcher contract is unaffected.

## Revisit criteria

Revisit if any of the below hold:

- A future agent runtime cannot speak A2A natively **and** cannot tolerate the agent-base bridge (e.g. WASM-only, or a runtime that hard-requires its own RPC). At that point implement `AgentResponseCapture.Stdout` or `.VolumeDrop` against that specific launcher and amend this ADR.
- A2A 0.4.x or 1.x lands and the `protocolVersion: "0.3"` pin in the spec needs to move. The wire contract bumps; the launcher contract does not.
- The dispatcher's readiness-probe overhead becomes a measurable hot-path cost (e.g. > 5% of dispatch latency at the p50 for ephemeral agents). The probe is currently 200ms-backoff polling; switching to "first message starts the readiness clock" is the obvious knob to turn.
