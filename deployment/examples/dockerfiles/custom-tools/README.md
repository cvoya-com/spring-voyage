# Custom-tools agent image

Extends the Spring Voyage agent base with extra CLI tooling the agent
process can shell out to. Use this template when your agent workflow
needs a tool that isn't shipped with
`localhost/spring-voyage-agent:latest` (the tag produced by
`deployment/deploy.sh build`).

## What this Dockerfile does

Inherits `localhost/spring-voyage-agent:latest`, switches to root long
enough to install extra packages via `apt-get`, then drops back to the
non-root `agent` user so the runtime identity matches the base image.

The file ships commented-out examples for three common shapes:

- **system package** — `hyperfine`, `protobuf-compiler`, anything
  available via Debian apt.
- **MCP server pinned via npm** — `@your-org/your-mcp-server`.

Pick the shape you need, un-comment it, and rebuild.

## Build

```
podman build -t localhost/my-agent-with-tools:latest .
# or: docker build -t localhost/my-agent-with-tools:latest .
```

## Reference it

### From a unit YAML manifest

```yaml
unit:
  name: platform-eng
  execution:
    image: localhost/my-agent-with-tools:latest
    runtime: podman
```

Every member agent that does not override `execution.image` will run
inside this image at dispatch. See `docs/architecture/units.md` for
the full agent → unit → fail resolution chain.

### From the portal

Open the unit, switch to the **Execution** tab, paste the image
reference into the **Image** field, optionally pick `podman` on the
**Runtime** dropdown, and save.

## Extension pattern

The base image runs as a non-root `agent` user. When you install
extra packages switch to root first and switch back with `USER agent`
so the container's default identity stays unprivileged:

```dockerfile
USER root
RUN apt-get update && apt-get install -y --no-install-recommends <pkg>
USER agent
```

Don't accumulate unused layers — each `apt-get install` that isn't
followed by `rm -rf /var/lib/apt/lists/*` bloats the image.

## Remote registries

For multi-host deployments push the image to a registry every host can
pull from:

```
podman push localhost/my-agent-with-tools:latest \
            ghcr.io/<org>/my-agent-with-tools:latest
```

Platform-side registry integration (searchable image catalog from the
portal) is tracked in #623.
