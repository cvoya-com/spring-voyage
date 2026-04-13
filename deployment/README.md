# Deployment

Container images used by the Spring Voyage platform.

## Contents

| File               | Purpose                                              |
| ------------------ | ---------------------------------------------------- |
| `Dockerfile.agent` | Slim image for delegated agent execution containers. |
| `Dockerfile.dev`   | Multi-stage dev image for local container mode.      |

## Agent Execution Image (`Dockerfile.agent`)

The agent image is the runtime for delegated execution mode (see
[Architecture — Units](../docs/architecture/units.md)). An agent actor in
the worker dispatches work to a container built from this image.

```bash
podman build -f deployment/Dockerfile.agent -t spring-voyage-agent .
```

The image provides a sandboxed shell with Node.js, Python, and common
tools. Agent-specific tools are layered on top in downstream images or
mounted at runtime.

## Dev Image (`Dockerfile.dev`)

Used by `scripts/dev.sh` in container mode. Builds the .NET hosts, Next.js
web dashboard, and includes the Dapr CLI.

```bash
./scripts/dev.sh build    # builds the dev image
./scripts/dev.sh up       # starts the local stack using it
```

## Production Deployment

Production deployment scripts (Podman stack, VPS deployment, Caddyfiles,
Kubernetes) live in the private Spring repository, not here.
See [docs/developer/setup.md](../docs/developer/setup.md) for local
development using `scripts/dev.sh`.
