# Deployment

> **[Architecture index](README.md)** · Related: [Components](components.md), [Runtime flows](runtime-flows.md), [Agent runtime](agent-runtime.md)

How Spring Voyage runs: agent hosting modes, the container topology, the
dispatcher, Dapr sidecar bootstrap, startup configuration, and releases.

---

## Agent hosting modes

Every agent runs in one of two modes, set by `AgentExecutionConfig.Hosting`:

| Mode | Lifecycle | Best for |
|------|-----------|----------|
| **Ephemeral** (default) | A fresh container per dispatch, torn down after the turn | Short, stateless turns; strongest isolation |
| **Persistent** | A long-lived container started once and kept alive | Expensive warm state, low-latency interactive agents |

Both dispatch through the same `A2AExecutionDispatcher` path (see
[Agent runtime](agent-runtime.md)) — persistence is a retention policy, not a
separate code path ([ADR-0025](../decisions/0025-unified-agent-launch-contract.md)).

`PersistentAgentRegistry` (worker-side) tracks every running persistent
container, health-probes it on a sweep, and restarts an unhealthy one from its
retained definition. A readiness timeout (60s) bounds startup; three consecutive
failed probes mark a container unhealthy.

## Container topology

Spring Voyage runs platform services on a `spring-net` bridge and every
tenant-aware container on a per-tenant network (`spring-tenant-<id>`; OSS uses a
single `spring-tenant-default`). The component picture is in
[Components](components.md). The worker stays single-homed on `spring-net` by
design — dual-homing would let every actor reach every tenant's namespace.
`spring-caddy` is dual-attached so tenant containers can reach the authenticated
REST API without crossing onto the platform network
([ADR-0028](../decisions/0028-tenant-scoped-runtime-topology.md)).

## The dispatcher

`spring-dispatcher` is a **host process**, not a container
([ADR-0012](../decisions/0012-spring-dispatcher-service-extraction.md)). It is
the only process that holds the host container binary; the worker's sole
`IContainerRuntime` binding, `DispatcherClientContainerRuntime`, forwards every
container operation to it over HTTP. It runs as a host process — rather than a
container — because the rootless Podman socket cannot be reliably bind-mounted
into a container on macOS/arm64, and a single topology across Linux/macOS/Windows
keeps local development predictable.

The dispatcher's HTTP contract covers run/start a container, read logs, stop and
remove, pull an image, create/remove a network, run a one-shot in-container HTTP
probe, and proxy an A2A `message/send` into a container. Every request carries a
bearer token mapped to a tenant scope. The OSS backend shells out to `podman`;
`DockerRuntime` ships in-tree, and a downstream deployment repo can plug a
Kubernetes backend behind the same HTTP contract.

### Per-invocation workspace materialisation

A launcher describes the workspace an agent needs as **pure data** — a file map
plus a mount path. The dispatcher materialises that workspace on its own host
filesystem before launching the container and bind-mounts it in. This is why the
worker carries no workspace mount of its own: the worker's filesystem is private
to its container and invisible to the host `podman` the dispatcher shells out
against. There is one per-agent persistent workspace mount
([ADR-0052](../decisions/archive/0052-execution-host-roles-and-single-mcp-server.md)).

### Dapr sidecar bootstrap

Workflow containers (and `spring-voyage`-runtime agents) need their own Dapr
sidecar. `ContainerLifecycleManager` + `DaprSidecarManager` compose this through
the dispatcher: create a per-workflow bridge network, start the `daprd` sidecar
dual-attached to that bridge and the tenant bridge, health-probe it, start the
workflow container, and tear it all down on exit. CLI-runtime agents
(`claude-code`, `codex`, `gemini`) need no per-container `daprd` — they speak
A2A to the dispatcher and reach platform tools through the worker's MCP route.

## Startup configuration validation

Each host validates its configuration at startup through the
`IConfigurationRequirement` framework. A requirement classifies a config concern
as **valid**, **disabled-with-reason** (the platform still boots; the affected
surface short-circuits cleanly), or **invalid** (a fatal error — the host
fail-fasts rather than failing on the first request). The classified results
feed a `/api/v1/platform/system/configuration` report. The GitHub connector's
credential check is the worked example (see [Connectors](connectors.md)).

Each host also fail-fasts on a composition error: if `Build()` or `RunAsync()`
throws, the host logs and `Environment.Exit(1)` so the orchestrator restarts it
rather than leaving a half-dead process reporting "Up".

## Releases

Reference agent-runtime images publish to GHCR from a tagged release:
`spring-voyage-claude-code-base` (Claude Code on the A2A sidecar bridge) and
`spring-voyage-agent` (the native A2A runtime). A maintainer runs the release
script, which pushes an immutable release tag and updates the floating `:X.Y` /
`:latest` tags for stable releases. Agent/unit definitions reference pre-built
images by tag; production deployments always pull from the registry.
