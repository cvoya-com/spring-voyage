# 0012 — Extract container-runtime ownership into a dedicated `spring-dispatcher` service

- **Status:** Accepted — the worker no longer holds the host container binary. Its only `IContainerRuntime` binding is `DispatcherClientContainerRuntime`, which forwards every launch / logs / stop call over HTTP to the new `Cvoya.Spring.Dispatcher` ASP.NET service. The dispatcher owns `PodmanRuntime` (OSS default); the HTTP contract is backend-plural so downstream deployment repositories can plug in a Kubernetes-native backend without touching the worker.
- **Date:** 2026-04-17
- **Closes:** [#521](https://github.com/cvoya-com/spring-voyage/issues/521)
- **Implemented by:** [PR #535](https://github.com/cvoya-com/spring-voyage/pull/535) (closes [#513](https://github.com/cvoya-com/spring-voyage/issues/513))
- **Related:** [#483](https://github.com/cvoya-com/spring-voyage/issues/483), [#504](https://github.com/cvoya-com/spring-voyage/issues/504), [#506](https://github.com/cvoya-com/spring-voyage/issues/506) (the security / deployment pressure that forced the extraction), [#522](https://github.com/cvoya-com/spring-voyage/issues/522) (follow-up to move the remaining host-podman surfaces behind the dispatcher), [ADR 0011](0011-persistent-agent-lifecycle-http-surface.md) (the lifecycle HTTP surface that dispatches through this seam).
- **Related code:** `src/Cvoya.Spring.Dispatcher/Program.cs`, `src/Cvoya.Spring.Dispatcher/ContainersEndpoints.cs`, `src/Cvoya.Spring.Dispatcher/BearerTokenAuthHandler.cs`, `src/Cvoya.Spring.Dispatcher/DispatcherContracts.cs`, `src/Cvoya.Spring.Dapr/Execution/DispatcherClientContainerRuntime.cs`, `Dockerfile.dispatcher`, `docker-compose.yml`, `docs/architecture/deployment.md` § *Dispatcher service*.

## Context

Through Phase 4 the worker process owned the host container runtime directly: `PodmanRuntime` was registered as the `IContainerRuntime` singleton in the worker, and every agent / workflow dispatch called `podman` against a socket mounted into the worker container. That shape was pragmatic for single-host dev but accumulated pressure from three directions:

1. **Tenant isolation (#483).** Mounting the container-runtime socket into the worker gives the worker the ability to launch arbitrary sibling containers. In any multi-tenant deployment, the worker is also the process that runs agent-authored state through dispatch — conflating those two roles violates least privilege. Socket passthrough was raised and rejected; nested podman and shared tmpfs were also explored and rejected (see below).
2. **Readiness-probe ergonomics (#504).** The worker had to know how to reach persistent-agent endpoints that it had just launched on the host network, which required threading hostname translation (`host.docker.internal`, gateway routing) through the worker's configuration.
3. **Kubernetes-native backends (#506).** Downstream deployment repositories targeting Kubernetes need to map `IContainerRuntime` onto the Kubernetes API (Pods, not podman processes). Keeping the runtime inside the worker forced every such deployment to either ship a custom worker image or carry a runtime shim — a cross-cutting fork point.

Design questions that needed answers before shipping:

1. **Service or in-process module?** An in-process `ContainerRuntimeBroker` inside the worker would share credentials; a separate service lets credentials live on one process and nothing else.
2. **HTTP contract or Dapr-to-Dapr?** The dispatcher is not an actor; it does not need Dapr. Plain ASP.NET + bearer tokens is smaller surface.
3. **Backend-singular or backend-plural?** Ship just podman (OSS) or design the HTTP contract so K8s, containerd, etc. can slot in?

## Decision

**Extract container-runtime ownership into a new `Cvoya.Spring.Dispatcher` ASP.NET service. The worker's only `IContainerRuntime` binding becomes `DispatcherClientContainerRuntime`, which speaks HTTP to the dispatcher. The HTTP contract is intentionally backend-plural; OSS ships the podman backend only.**

### HTTP surface

Three authenticated endpoints plus an unauthenticated health probe:

| Verb     | Path                             | Purpose                                     |
| -------- | -------------------------------- | ------------------------------------------- |
| `POST`   | `/v1/containers`                 | Start a container (image, env, mounts, …).  |
| `GET`    | `/v1/containers/{id}/logs`       | Read combined stdout/stderr (tail-bounded). |
| `DELETE` | `/v1/containers/{id}`            | Stop and remove a container.                |
| `GET`    | `/health`                        | Unauthenticated liveness probe.             |

Every authenticated request carries `Authorization: Bearer <token>`. Tokens are opaque strings configured at deploy time through `Dispatcher__Tokens__<token>__TenantId=<tenant>`; the mapping is the scope the request can assert. Unauthenticated calls 401; unknown tokens 401; cross-tenant violations (once tenant-aware scoping enforces the map at the authorisation layer) 403.

### Worker binding

The worker registers `DispatcherClientContainerRuntime` as the sole `IContainerRuntime` and removes every other binding. The client builds the HTTP request, forwards the bearer token from configuration, and surfaces dispatcher errors as typed exceptions the existing launchers already understand. No `A2AExecutionDispatcher` or `IAgentToolLauncher` code changed — the seam is under them.

### Backend plurality

Inside the dispatcher, `IContainerRuntime` is still the extensibility seam. OSS registers `PodmanRuntime` (a thin wrapper over `ProcessContainerRuntime` that shells out to `podman-remote` against a rootless socket). `DockerRuntime` is also in-tree for operators who prefer Docker. Downstream deployment repositories register their own backend (e.g. a `KubernetesPodRuntime` that maps `StartAsync` to a Pod creation) and nothing in the worker changes.

## Alternatives considered

- **Socket passthrough (#483).** Mount the container socket into every worker. Cheapest to implement, worst isolation posture: the worker can launch any container and breaks tenant boundaries in shared-host deployments. **Rejected.**
- **Nested podman (#483 option 2).** Run a podman daemon inside the worker container. Fixes the shared-socket problem but doubles the privileged-container footprint and still leaves runtime credentials in the worker. **Rejected.**
- **Shared tmpfs handoff (#504).** Worker writes launch manifests to a tmpfs; a sidecar daemon picks them up. Avoids HTTP but reintroduces filesystem IPC, ordering, and back-pressure problems the HTTP contract handles for free. **Rejected.**
- **In-process broker.** Put the podman logic behind a broker interface inside the worker. Solves the abstraction problem but not the privilege problem. **Rejected.**

## Consequences

- **The worker loses its most dangerous capability.** Container-launch rights move to a single process whose only job is to mediate them. Tenant isolation follows the dispatcher's authorisation layer, not the worker's container image.
- **Downstream deployments can ship Kubernetes-native backends without forking the worker.** The HTTP contract is the seam; the worker sees only `IContainerRuntime`.
- **OSS scope stays single-host.** The OSS stack ships a single dispatcher token scoped to the `default` tenant. Multi-tenant K8s deployments live downstream; nothing in OSS assumes multiple tenants exist.
- **`PersistentAgentLifecycle` (ADR 0011) and `A2AExecutionDispatcher` go through the same seam.** Both resolve `IContainerRuntime` and both get the HTTP-backed implementation; the operator lifecycle surface and the turn-dispatch path share one runtime binding.
- **An HTTP hop is now on the dispatch hot path.** The dispatcher runs on the same host in OSS deployments so the latency cost is a local TCP call; the benefit — one process holds runtime credentials — is worth it.
- **Follow-up #522 finishes the migration.** `ContainerLifecycleManager`, `DaprSidecarManager`, and ad-hoc network operations still talk to podman directly from the worker in a few places; they move behind the dispatcher in a later PR so this one stays diff-bounded.
