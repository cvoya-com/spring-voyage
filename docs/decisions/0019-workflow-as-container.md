# 0019 — Domain workflows run as containers, not in-process

- **Status:** Accepted — domain workflows ship as container images with their own Dapr sidecars; only platform-internal lifecycle workflows are compiled into the host.
- **Date:** 2026-04-21
- **Related code:** `src/Cvoya.Spring.Dapr/Workflows/` (platform-internal), `deployment/examples/dockerfiles/` (workflow-container patterns).
- **Related docs:** [`docs/architecture/workflows.md`](../architecture/workflows.md), [ADR 0015](0015-dapr-as-infrastructure-runtime.md).

## Context

Spring Voyage units that pick the workflow orchestration strategy delegate sequencing to a long-running, durable workflow. Two shapes were on the table:

1. **Compile every workflow into the host.** All workflows live in the platform binary; deploying a workflow change means redeploying the platform.
2. **Workflows as containers.** Each domain workflow is an independent container image with its own Dapr sidecar; the platform invokes it through the dispatcher path.

V1 had no comparable feature, so there was no migration constraint. The v2 design goal — domain-agnostic, with packages contributing workflows — made the second shape much more attractive on first principles, but we needed to be honest about the cost.

## Decision

**Domain workflows run as containers with their own Dapr sidecars. Only a small set of platform-internal lifecycle workflows (agent creation, cloning, validation orchestration) are compiled into the host because they are platform concerns, not domain concerns.**

- **Decoupled releases.** A package author iterates on its workflow image without redeploying the platform. The platform's release cadence is decoupled from the cadence of every domain it serves.
- **Workflow engine choice is the container's.** The container can run Dapr Workflows, Temporal, a hand-rolled state machine — the platform doesn't care. It only sees the dispatcher contract on the way in.
- **In-flight safety.** Existing workflow instances complete on the old container image; new instances spin up against the new image. There is no in-flight disruption from a deploy.
- **Same dispatcher path as agent runtimes.** A workflow container is launched by the dispatcher service ([ADR 0012](0012-spring-dispatcher-service-extraction.md)) the same way an agent runtime is. The host process holds zero container-runtime credentials.

## Alternatives considered

- **Compile all workflows into the host.** Simplest implementation, but every domain change requires a platform release. Rejected on the v2 design goal of "platform stable, domains iterate independently."
- **Shared workflow service.** Run one long-lived workflow process per cluster; route workflow invocations to it. Couples every workflow's lifecycle to one process; an OOM or rolling deploy of the workflow service drops every running domain workflow.

## Consequences

- **Container-runtime cost.** Every workflow invocation pulls (or hits the local cache for) an image. Acceptable — we already pay this for agent runtimes and the dispatcher already exists.
- **Platform-internal workflow exception is explicit.** If a workflow is genuinely a platform concern (lifecycle, validation orchestration) it lives in the host; everything else lives in a container. The line is enforced by review, not by build tooling.
- **Workflow images are first-class operator artefacts.** Operators care about which workflow image runs in production, can pin tags, and can register custom images per package.
- **Failure modes are container-shaped.** A failed workflow looks like a failed container — same logs, same retry semantics, same observability surface as agent runtimes.
