# Agent Image Taxonomy

Spring Voyage ships two classes of container image for running agents:

| Image | GHCR reference | Purpose |
|-------|----------------|---------|
| `spring-voyage-agent-base` | `ghcr.io/cvoya-com/spring-voyage-agent-base:latest` | BYOI minimal — the A2A sidecar bridge only. Operators layer their own CLI on top. |
| `spring-voyage-agents` | `ghcr.io/cvoya-com/spring-voyage-agents:latest` | Omnibus default — all OSS runtime CLIs pre-installed. The wizard default. |

Per-runtime images (`spring-voyage-agent-claude-code`, `spring-voyage-agent-dapr`) exist for single-runtime deployments or CI build verification and are not published to GHCR by default.

## spring-voyage-agent-base (BYOI minimal)

**Source:** `deployment/Dockerfile.agent-base`
**Published by:** `release-agent-base.yml` on `agent-base-v*` tags.

The minimal layer an operator needs to plug any CLI into the Spring Voyage dispatcher:

- `debian:12-slim` base OS.
- Node.js 22 + the compiled TypeScript A2A bridge sidecar on `:8999`.
- `tini` as PID 1 for clean signal forwarding.
- Non-root `agent` user (uid/gid 1000).

**When to use `agent-base`:** When you are implementing BYOI conformance path 1 (a custom CLI that is not one of the four OSS runtimes). Extend it with a single `FROM` + `RUN npm install -g <your-cli>` layer.

**Example:**

```dockerfile
FROM ghcr.io/cvoya-com/spring-voyage-agent-base:latest
USER root
RUN npm install -g my-private-agent-cli@1.2.3
USER agent
```

## spring-voyage-agents (omnibus default)

**Source:** `deployment/Dockerfile.spring-voyage-agents`
**Published by:** `release-spring-voyage-agents.yml` on `agents-v*` tags.
**Wizard default:** The unit-creation wizard pre-fills this image when no runtime-specific image is set.

The omnibus image layers all four OSS CLI runtimes on top of `spring-voyage-agent-base`:

| CLI | npm package | Version (pinned) | Runtime ID |
|-----|-------------|-----------------|------------|
| `claude` | `@anthropic-ai/claude-code` | 2.1.98 | `claude` |
| `codex` | `@openai/codex` | 0.128.0 | `openai` |
| `gemini` | `@google/gemini-cli` | 0.40.1 | `google` |
| Python dapr-agent stack | PyPI (see `agents/dapr-agent/requirements.txt`) | pinned in requirements | `openai`, `google`, `ollama` (via `dapr-agent` tool kind) |

Version ARGs are declared at the top of the Dockerfile and recorded in image labels (`com.cvoya.spring-voyage.*-version`) so `docker inspect` surfaces the exact CLI versions without a `docker exec`.

### Runtime dispatch

The dispatcher stamps `SPRING_AGENT_ARGV` at launch time. The A2A bridge (inherited from `agent-base`) reads this env var and spawns the correct CLI:

```
SPRING_AGENT_ARGV='["claude","--dangerously-skip-permissions"]'
SPRING_AGENT_ARGV='["codex","--full-auto"]'
SPRING_AGENT_ARGV='["gemini","--yolo"]'
SPRING_AGENT_ARGV='["python","/opt/spring-voyage/dapr-agent/agent.py"]'
```

The entrypoint is **not** overridden — it is inherited from `agent-base` (`tini → node sidecar`).

### When to use the omnibus

Use `spring-voyage-agents:latest` when:
- You are creating a unit and do not need a custom CLI layer.
- You want to switch runtimes (Claude → OpenAI → Google) without rebuilding an image.
- You are evaluating the platform for the first time.

## Per-runtime images

Built by `deployment/build-agent-images.sh` for local dev and CI verification.

| Image (local tag) | Source file | Tool kind |
|-------------------|-------------|-----------|
| `localhost/spring-voyage-agent-claude-code:dev` | `deployment/Dockerfile.agent.claude-code` | `claude-code-cli` |
| `localhost/spring-voyage-agent-dapr:dev` | `deployment/Dockerfile.agent.dapr` | `dapr-agent` (native A2A, Python) |

**When to use per-runtime images:**
- Smaller attack surface / image size for deployments where only one CLI is needed.
- CI pipelines that verify a specific CLI version in isolation.
- The `dapr-agent` image implements BYOI conformance **path 3** (native A2A in Python) and is the reference for the OpenAI, Google, and Ollama runtimes when not using the omnibus.

## Extension patterns

### Extending spring-voyage-agents (add domain tooling)

Layer your toolchain on top of the omnibus without replacing the sidecar bridge:

```dockerfile
FROM ghcr.io/cvoya-com/spring-voyage-agents:latest
USER root
# Example: add a domain-specific dotnet SDK
RUN apt-get update \
 && apt-get install -y --no-install-recommends dotnet-sdk-9.0 \
 && rm -rf /var/lib/apt/lists/*
USER agent
```

### Extending spring-voyage-agent-base (BYOI custom agent)

For a completely custom agent CLI that is not one of the OSS runtimes:

```dockerfile
FROM ghcr.io/cvoya-com/spring-voyage-agent-base:latest
USER root
RUN npm install -g my-private-agent-cli@1.2.3 \
 && command -v my-agent >/dev/null
USER agent
```

Set `SPRING_AGENT_ARGV='["my-agent","--flag"]'` at launch time.

## Pull and verify commands

```bash
# Pull the omnibus default
docker pull ghcr.io/cvoya-com/spring-voyage-agents:latest

# Verify the bundled CLIs (runs each binary's version command)
docker run --rm ghcr.io/cvoya-com/spring-voyage-agents:latest \
  sh -c 'claude --version; codex --version; gemini --version; python --version'

# Inspect version labels without a shell exec
docker inspect ghcr.io/cvoya-com/spring-voyage-agents:latest \
  --format '{{json .Config.Labels}}' | jq .

# Pull the BYOI minimal base
docker pull ghcr.io/cvoya-com/spring-voyage-agent-base:latest
```
