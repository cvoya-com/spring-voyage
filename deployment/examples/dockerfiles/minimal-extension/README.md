# Minimal agent image extension

The smallest possible custom agent image: re-tag one of the
tool-bearing agent images shipped by `deployment/build-agent-images.sh`
(the entry point added in PR 3b of #1087, #1096) under your own name so
unit / agent manifests reference a stable, pinned identifier.

The example below extends `localhost/spring-voyage-agent-claude-code:latest`
(BYOI conformance path 1 — agent-base bridge + Claude Code CLI).
Substitute `localhost/spring-voyage-agent-dapr:latest` for the Dapr
Agent path-3 runtime, or `ghcr.io/cvoya-com/agent-base:1.0.0` if you only
want the bridge sidecar and will install your own CLI on top.

## What this Dockerfile does

Inherits the chosen agent base image unchanged and produces a new image
under your chosen tag. No extra tooling is added. Use this as a starting
point when you want to pin a tag but don't yet need additional CLI tools
or MCP servers in the image.

## Build

```
podman build -t localhost/my-agent:latest .
# or: docker build -t localhost/my-agent:latest .
```

Replace `localhost/my-agent:latest` with whatever registry / name you
prefer; for single-host deployments the `localhost/` prefix keeps the
image in the local rootless-podman store.

## Reference it

### From a unit YAML manifest

```yaml
unit:
  name: my-team
  execution:
    image: localhost/my-agent:latest
```

The unit execution block acts as the default for every member agent
that does not declare its own image. See
`docs/architecture/units.md` for the full resolution chain.

### From the portal

Open the unit, switch to the **Execution** tab, paste the image
reference into the **Image** field, and save.

## Remote registries

For multi-host deployments push the image to a registry every host can
pull from, then reference the fully-qualified name:

```
podman push localhost/my-agent:latest ghcr.io/<org>/my-agent:latest
# unit.yaml
execution:
  image: ghcr.io/<org>/my-agent:latest
```

Registry hosting and pull-secret configuration are outside the OSS
scope — see your registry provider's documentation. Platform-side
registry integration is tracked in #623.
