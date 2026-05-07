# Agent Image Taxonomy

Spring Voyage ships two classes of container image for running agents:

| Image | GHCR reference | Purpose |
|-------|----------------|---------|
| `agent-base` | `ghcr.io/cvoya-com/agent-base:latest` | BYOI minimal — the A2A sidecar bridge only. Operators layer their own CLI on top. |
| `spring-voyage-agent` | `ghcr.io/cvoya-com/spring-voyage-agent:latest` | Path-3 native A2A image used by the `spring-voyage` runtime. |

Per ADR-0038, every entry under `agentRuntimes` in `platform/runtime-catalog.yaml` declares a `defaultImage`. The unit-creation wizard pre-fills the image field with the selected runtime's `defaultImage`. The per-runtime base images for the OSS CLI runtimes (`claude-code-base`, `codex-base`, `gemini-base`) are upstream images and are not built or published by this repository.

## agent-base (BYOI minimal)

**Source:** `deployment/Dockerfile.agent-base`
**Published by:** `release-agent-base.yml` on `agent-base-v*` tags.

The minimal layer an operator needs to plug any CLI into the Spring Voyage dispatcher:

- `python:3.13-slim` base (Debian trixie + Python 3.13).
- Node.js 22 + the compiled TypeScript A2A bridge sidecar on `:8999`.
- `tini` as PID 1 for clean signal forwarding.
- Non-root `agent` user (uid/gid 1000).

**When to use `agent-base`:** When you are implementing BYOI conformance path 1 (a custom CLI that is not one of the OSS runtimes) or as the base for a role-flavored image. Extend it with a single `FROM` + `RUN npm install -g <your-cli>` layer.

**Example:**

```dockerfile
FROM ghcr.io/cvoya-com/agent-base:latest
USER root
RUN npm install -g my-private-agent-cli@1.2.3
USER agent
```

## Per-runtime images

Built by `deployment/build-agent-images.sh` for local dev and CI verification.

| Image (local tag) | Source file | Tool kind |
|-------------------|-------------|-----------|
| `localhost/spring-voyage-agent-claude-code:dev` | `deployment/Dockerfile.agent.claude-code` | `claude-code-cli` |
| `localhost/spring-voyage-agent:dev` | `deployment/Dockerfile.agent.dapr` | `spring-voyage-agent` (native A2A, Python) |

**When to use per-runtime images:**
- Smaller attack surface / image size for deployments where only one CLI is needed.
- CI pipelines that verify a specific CLI version in isolation.
- The `spring-voyage-agent` image implements BYOI conformance **path 3** (native A2A in Python) and is the reference image for the `spring-voyage` runtime.

## Extension patterns

### Extending for a specific runtime

Layer your toolchain on top of the runtime's `defaultImage` from `platform/runtime-catalog.yaml`. For example, the OSS dogfooding role images extend `spring-voyage-agent-base` and install the Claude Code CLI alongside role-specific tools:

```dockerfile
FROM ghcr.io/cvoya-com/claude-code-base:latest
USER root
# Example: add a domain-specific dotnet SDK
RUN apt-get update \
 && apt-get install -y --no-install-recommends dotnet-sdk-9.0 \
 && rm -rf /var/lib/apt/lists/*
USER agent
```

### Extending agent-base (BYOI custom agent)

For a completely custom agent CLI that is not one of the OSS runtimes:

```dockerfile
FROM ghcr.io/cvoya-com/agent-base:latest
USER root
RUN npm install -g my-private-agent-cli@1.2.3 \
 && command -v my-agent >/dev/null
USER agent
```

Set `SPRING_AGENT_ARGV='["my-agent","--flag"]'` at launch time.

## Pull and verify commands

All images are publicly available on GHCR — no credentials required.

```bash
# Pull the BYOI minimal base
docker pull ghcr.io/cvoya-com/agent-base:latest

# Pull the path-3 native-A2A image
docker pull ghcr.io/cvoya-com/spring-voyage-agent:latest
```
