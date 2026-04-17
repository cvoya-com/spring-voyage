# 0011 — Persistent-agent lifecycle as a distinct HTTP surface (and CLI verbs) on top of `PersistentAgentRegistry`

- **Status:** Accepted — `spring agent deploy / scale / logs / undeploy` (and the matching `POST /api/v1/agents/{id}/deploy|undeploy|scale`, `GET /api/v1/agents/{id}/logs|deployment` endpoints) delegate to a new `PersistentAgentLifecycle` service that shares `PersistentAgentRegistry` with the turn-dispatch path.
- **Date:** 2026-04-16
- **Closes:** [#396](https://github.com/cvoya-com/spring-voyage/issues/396)
- **Related:** [#334](https://github.com/cvoya-com/spring-voyage/issues/334) (persistent hosting umbrella), [#390](https://github.com/cvoya-com/spring-voyage/issues/390) (spring-agent image that backs the default persistent deployment), [#362](https://github.com/cvoya-com/spring-voyage/issues/362) (container pooling / horizontal scale — tracked separately).
- **Related code:** `src/Cvoya.Spring.Dapr/Execution/PersistentAgentLifecycle.cs`, `src/Cvoya.Spring.Dapr/Execution/PersistentAgentRegistry.cs`, `src/Cvoya.Spring.Dapr/Execution/A2AExecutionDispatcher.cs`, `src/Cvoya.Spring.Host.Api/Endpoints/AgentEndpoints.cs`, `src/Cvoya.Spring.Host.Api/Models/PersistentAgentModels.cs`, `src/Cvoya.Spring.Cli/Commands/AgentCommand.cs`, `src/Cvoya.Spring.Cli/ApiClient.cs`.

## Context

`PersistentAgentRegistry` (shipped under the #334 umbrella) already tracks running persistent agents, exposes a readiness probe, and restarts unhealthy containers on a timer. The only way to interact with it today is through `A2AExecutionDispatcher.DispatchAsync` — i.e. by sending the agent a message. Operators have no way to:

- Stand up a persistent agent container on purpose, before any inbound traffic.
- Read the current container health and endpoint without triggering a turn.
- Stream the agent's container logs for debugging.
- Tear a persistent agent down without also deleting its `agent://` record.

Three design questions had to be resolved before shipping a CLI surface:

1. **Does the dispatcher grow new entry points, or is there a new service?** The dispatcher's contract today is "given a message, produce a reply." Teaching it to expose operational verbs (`deploy`, `logs`) would conflate message-flow with container-management.
2. **Does `delete` do the container teardown, or is there a separate `undeploy`?** The acceptance on #396 explicitly calls out that `delete` and `undeploy` are distinct: `delete` removes the agent record, `undeploy` stops the container.
3. **How are the CLI verbs shaped when horizontal scale (#362) isn't shipped yet?** If `scale --replicas 2` is accepted today, the wire contract ossifies around a shape we can't support; if it's not accepted at all, we'll have to change the CLI later.

## Decision

**Add a new `PersistentAgentLifecycle` service (in `Cvoya.Spring.Dapr`) that owns the imperative operator surface and shares `PersistentAgentRegistry` with `A2AExecutionDispatcher`.** The service assembles an `AgentLaunchContext`, invokes the matching `IAgentToolLauncher.PrepareAsync`, starts a container via `IContainerRuntime.StartAsync`, probes A2A readiness through the existing `PersistentAgentRegistry.WaitForA2AReadyAsync`, and registers the entry. Undeploy calls a new public `PersistentAgentRegistry.UndeployAsync` that stops the container and drops the entry. The dispatcher's first-dispatch auto-start path is unchanged; operators now have an explicit alternative.

### HTTP surface

Five new endpoints under `/api/v1/agents/{id}`:

| Verb   | Path          | Body                                      | 200 shape                               |
| ------ | ------------- | ----------------------------------------- | --------------------------------------- |
| `POST` | `/deploy`     | `DeployPersistentAgentRequest` (optional) | `PersistentAgentDeploymentResponse`     |
| `POST` | `/undeploy`   | none                                      | `PersistentAgentDeploymentResponse`     |
| `POST` | `/scale`      | `ScalePersistentAgentRequest`             | `PersistentAgentDeploymentResponse`     |
| `GET`  | `/logs?tail=` | —                                         | `PersistentAgentLogsResponse`           |
| `GET`  | `/deployment` | —                                         | `PersistentAgentDeploymentResponse`     |

The existing `GET /api/v1/agents/{id}` status endpoint is extended to include an optional `deployment` slot carrying `PersistentAgentDeploymentResponse`, so `spring agent status <id>` is a single call for operators who want both actor status and container state.

### CLI verbs

```
spring agent deploy   <id> [--image <image>] [--replicas 0|1]
spring agent undeploy <id>
spring agent scale    <id> --replicas 0|1
spring agent logs     <id> [--tail N]
spring agent status   <id>   # extended to show deployment info
```

`spring agent delete <id>` is unchanged — it removes the directory entry. Operators are expected to `undeploy` first; the server does not auto-teardown on delete because the two surfaces deliberately stay orthogonal.

### Replica-count shape

The request bodies carry `Replicas` so the contract is stable for the horizontal-scale follow-up. The server accepts `{0, 1}` only today; anything else returns a 400 with a clear "not supported yet" message. `--replicas 0` is equivalent to `undeploy` so operators can collapse the two verbs when scripting.

### Image override

`deploy --image <image>` applies the override to the single deployment only — the stored `execution.image` on the agent definition is untouched. Useful for smoke-testing candidate images without editing the YAML.

## Consequences

### Positive

- **Operators can run a persistent agent without sending it a message.** This unblocks the #334 / #390 happy path where a persistent `spring-agent` container should come up before any inbound traffic (e.g. for warmup, certificate load, or image pre-pull).
- **Dispatch code stays focused.** `A2AExecutionDispatcher` continues to own "there is a message to run." The operator surface lives in `PersistentAgentLifecycle` so one can be refactored without destabilising the other.
- **Undeploy vs delete stays explicit.** A persistent agent can be torn down for maintenance (`undeploy`) and brought back (`deploy`) without losing its `agent://` record, memberships, or history.
- **Forward compatible with horizontal scale.** The `Replicas` slot is already on the wire; #362 / container pooling lands as a server-side behaviour change without a CLI re-shape.
- **Extended `status` stays backward compatible.** `AgentDetailResponse.Deployment` is optional and defaults to `null`; every older client keeps working.

### Negative

- **Two surfaces can race on the same container.** The auto-start path in the dispatcher and the imperative `deploy` both call `PersistentAgentRegistry.Register`. The registry is a `ConcurrentDictionary` so "last writer wins", and `DeployAsync`'s idempotent fast-path short-circuits when a healthy entry already exists — but a deploy-while-turn-is-starting window is theoretically possible. In practice the registry's health monitor reconciles quickly and the window is bounded by the readiness timeout; a follow-up could add a per-agent lock if the race surfaces in production.
- **Log reads are snapshots, not streams.** `spring agent logs --tail N` returns the last N lines of the container's combined output, not a live tail. Streaming (`docker logs --follow`) would require either a chunked HTTP endpoint or a WebSocket surface; #396 doesn't require it, so we didn't ship it.
- **The CLI's default exit code surfaces HTTP errors via Kiota exceptions.** The e2e scenario for `agent deploy <misconfigured-agent>` asserts a non-zero exit, not a specific code, because Kiota's ApiException flows through a generic catch in `Program.cs`. A future PR tightening exit codes would give scripts cleaner control flow; it is out of scope here.

### Neutral

- **`/api/v1/agents/{id}/deployment` duplicates part of `/status`.** We ship both because the `/status` endpoint sends a `StatusQuery` to the actor (a full round-trip), while `/deployment` is a pure read off the registry cache. Operators who want a cheap "is this agent up" probe now have one; the `/status` path stays authoritative for the richer view.
- **`PersistentAgentRegistry.UndeployAsync` made `GetAllEntries` public.** The previously-internal diagnostic method is now public so the endpoint layer can read the registry without reaching through a test-only surface. This is a minor expansion of the public API surface; callers that want to enumerate entries were already reaching for the internal method via tests.

## Alternatives considered

### Put lifecycle methods on `A2AExecutionDispatcher`

Cheaper to write — the dispatcher already knows every collaborator — but it overloads `IExecutionDispatcher` with operator semantics. The long-term direction is that other dispatchers (for non-A2A, non-container runtimes) may appear; keeping lifecycle on a dedicated service means those dispatchers don't have to carry an interface they can't implement. Rejected.

### Extend `spring agent delete` to stop the container

Matches the way `spring unit delete` cascades, but `#396` acceptance explicitly requires `undeploy` to be distinct. Operators asked for the ability to redeploy without re-creating the record. Rejected.

### Stream logs over SSE / WebSocket

The richer UX, but out of scope for #396 — the acceptance asks for `--tail N`. The wire shape we ship (`PersistentAgentLogsResponse`) leaves room for a streaming counterpart (`/logs/stream`) as a forward-compatible addition. Deferred.
