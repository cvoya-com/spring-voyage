# Bring Your Own Image (BYOI) — agent images for Spring Voyage

> **Audience.** Operators and platform engineers who want to ship a custom agent container image — pre-baked with proprietary CLIs, internal trust anchors, a non-Debian distro, or rootless / non-default UID models — and have Spring Voyage's `A2AExecutionDispatcher` invoke it the same way it invokes the built-in launchers (Claude Code, Codex, Gemini, Dapr Agent).

> **Scope.** Step-by-step recipes for each of the three conformance paths defined in [ADR 0027](../../decisions/0027-agent-image-conformance-contract.md), with copy-pasteable Dockerfile snippets, the launcher env-var contract (`SPRING_AGENT_ARGV`, `SPRING_MCP_ENDPOINT`, …), version compatibility rules, and debugging tips.

---

## TL;DR

A Spring Voyage agent image conforms when, after the dispatcher launches it, the running container exposes:

- **A2A 0.3.x** at `http://0.0.0.0:${AGENT_PORT}/` (default `8999`).
- An **Agent Card** at `GET /.well-known/agent.json` (alias: `/.well-known/agent-card.json`) whose `protocolVersion` is `"0.3"`.
- A response header `x-spring-voyage-bridge-version: <semver>` on every response (and the same field on the Agent Card / task payload).
- Implementations of A2A `message/send`, `tasks/cancel`, and `tasks/get` on `POST /`.
- Honour the launcher-supplied environment, including any `SPRING_*` keys (`SPRING_AGENT_ARGV`, `SPRING_MCP_ENDPOINT`, `SPRING_AGENT_TOKEN`, `SPRING_SYSTEM_PROMPT`, …).

Pick **one** of three paths to satisfy that contract:

| Path | Recipe                                                                                                                                                     |
|------|------------------------------------------------------------------------------------------------------------------------------------------------------------|
| 1    | `FROM ghcr.io/cvoya-com/agent-base:<semver>` and `RUN`-install your CLI tool. The bridge is the image's ENTRYPOINT and runs on `:8999` automatically.       |
| 2    | Pull the bridge into a custom base. Either `npm i -g @cvoya/spring-voyage-agent-sidecar` (Node-bearing image), or copy the static binary from each GitHub Release into a Node-less image. Set the bridge as the ENTRYPOINT. |
| 3    | Implement A2A 0.3.x natively in your image. No bridge involved.                                                                                            |

Path 1 is the default. Pick path 2 when you can't use the recommended base. Pick path 3 when your runtime already speaks A2A natively (e.g. `dapr-agents`).

---

## Background: how the dispatcher launches your image

When a turn arrives for an `agent://<path>` whose `execution.tool` matches your launcher (or one of the built-in launchers — Claude Code uses path 1, Dapr Agent uses path 3), the dispatcher executes the unified path documented in [ADR 0025](../../decisions/0025-unified-agent-launch-contract.md):

```text
A2AExecutionDispatcher.DispatchAsync(message, context)
  → IAgentDefinitionProvider.GetByIdAsync(agentId)
  → IPromptAssembler.AssembleAsync(message, context)
  → IMcpServer.IssueSession(agentId, conversationId)
  → launcher.PrepareAsync(launchContext)             // returns AgentLaunchSpec
  → ContainerConfigBuilder.Build(image, spec)        // single seam to ContainerConfig
  → IContainerRuntime.StartAsync (detached)          // ephemeral OR persistent: same call
  → poll GET /.well-known/agent.json on :A2APort     // readiness probe (60s budget, 200ms backoff)
  → A2AClient.SendMessageAsync(SendMessageRequest)   // A2A roundtrip, both modes
  → MapA2AResponseToMessage(...)                     // A2A response → Spring message
  → ephemeral: EphemeralAgentRegistry.ReleaseAsync   // teardown on turn drain
    persistent: leave running, registered in PersistentAgentRegistry
```

Two consequences for image authors:

1. **Your image is launched detached.** The dispatcher doesn't keep a foreground process. PID 1 inside your container has to either *be* the bridge (paths 1/2) or speak A2A natively (path 3). It must NOT be `sleep infinity` or any other "wait forever" stub — the dispatcher reaches your endpoint via the readiness probe, not by exec'ing into the container.
2. **The bind-mounted workspace is per-invocation.** The dispatcher writes any files declared in `AgentLaunchSpec.WorkspaceFiles` into a fresh directory on its own host filesystem and bind-mounts that directory at `WorkspaceMountPath` (typically `/workspace`) inside the container. Your image must not assume a specific workspace already exists in the image; it gets a fresh one every turn.

---

## Path 1 — `FROM ghcr.io/cvoya-com/agent-base`

This is the recommended path. The `agent-base` image bundles `tini`, Node 22, and a pre-installed copy of the A2A bridge under `/opt/spring-voyage/sidecar/`. The image's ENTRYPOINT runs the bridge on `:8999`. You add your CLI tool on top.

```dockerfile
# syntax=docker/dockerfile:1.7
FROM ghcr.io/cvoya-com/agent-base:1.0.0

# Install the CLI tool you want the agent to invoke. The bridge spawns
# whatever is in SPRING_AGENT_ARGV at message/send time, so anything that
# resolves on PATH inside the container will work.
RUN npm install -g @anthropic-ai/claude-code

# Done. ENTRYPOINT is inherited from the base image.
```

Build and tag:

```bash
docker build -t ghcr.io/my-org/my-claude-image:dev -f Dockerfile .
```

That's the entire path 1 recipe. The bridge is the image's PID 1; the dispatcher dials `:8999`; your CLI is spawned per `message/send` with the argv vector the launcher stamps into `SPRING_AGENT_ARGV`.

The built-in `Cvoya.Spring.Dapr.Execution.ClaudeCodeLauncher` is the canonical example of how a launcher pairs with a path-1 image:

- It leaves `AgentLaunchSpec.Argv` empty so the base image's ENTRYPOINT (the bridge) takes over.
- It stamps `SPRING_AGENT_ARGV` with the CLI argv (`["claude","--print","--dangerously-skip-permissions","--output-format","stream-json"]`).
- It writes `CLAUDE.md` (the assembled prompt) and `.mcp.json` (the MCP endpoint + token) into the workspace.
- It puts the same prompt on `AgentLaunchSpec.StdinPayload` so the bridge feeds it to `claude`'s stdin.

A custom launcher pointed at your image would mirror that shape: leave `Argv` empty, set `SPRING_AGENT_ARGV` to whatever your CLI needs, and (optionally) set `StdinPayload` if your CLI consumes stdin.

---

## Path 2 — pull the bridge into a custom base

Pick this path when path 1 doesn't fit: you need a non-Debian distro, your image runs as a non-default UID and you can't reuse the base's user, you can't allow Node in the runtime layer, or you're targeting an arch the base image doesn't publish.

The bridge is distributed two ways:

- **npm package**: `@cvoya/spring-voyage-agent-sidecar` (works on any Node-bearing image).
- **Single-executable binaries**: attached to each GitHub Release (`spring-voyage-agent-sidecar-linux-amd64`, `spring-voyage-agent-sidecar-linux-arm64`, `spring-voyage-agent-sidecar-darwin-arm64`). No Node runtime required.

### Path 2a — npm install (Node-bearing image)

```dockerfile
# syntax=docker/dockerfile:1.7
FROM node:22-alpine

# Install the bridge globally.
RUN npm install -g @cvoya/spring-voyage-agent-sidecar@1.0.0

# Install your CLI tool (whatever is appropriate for your distro).
RUN apk add --no-cache python3 py3-pip \
 && pip install --break-system-packages your-cli==1.2.3

EXPOSE 8999
ENTRYPOINT ["spring-voyage-agent-sidecar"]
```

The in-tree smoke fixture at [`tests/fixtures/byoi-path2/Dockerfile`](../../../tests/fixtures/byoi-path2/Dockerfile) is the same recipe sourced from `npm pack` of the working tree instead of `npmjs.org` — useful if you want to see the exact CI shape end-to-end, or if you're iterating on the bridge itself and want to smoke a local change before publishing. See `tests/scripts/smoke-1087.sh --path 2` for the driver.

### Path 2b — SEA binary (Node-less image)

```dockerfile
# syntax=docker/dockerfile:1.7
FROM cgr.dev/chainguard/wolfi-base

ARG BRIDGE_VERSION=1.0.0
ARG TARGETARCH

# Pull the SEA binary for the right arch. The mapping is:
#   amd64 → linux-amd64
#   arm64 → linux-arm64
RUN apk add --no-cache curl ca-certificates \
 && case "${TARGETARCH}" in \
      amd64) target="linux-amd64" ;; \
      arm64) target="linux-arm64" ;; \
      *) echo "unsupported arch: ${TARGETARCH}" >&2; exit 1 ;; \
    esac \
 && curl -fsSL -o /usr/local/bin/spring-voyage-agent-sidecar \
      "https://github.com/cvoya-com/spring-voyage/releases/download/agent-base-v${BRIDGE_VERSION}/spring-voyage-agent-sidecar-${target}" \
 && chmod +x /usr/local/bin/spring-voyage-agent-sidecar

# Install your CLI on top — this image is Node-less, so the bridge runs
# as a self-contained binary and your CLI can be anything.
COPY ./bin/your-cli /usr/local/bin/your-cli

EXPOSE 8999
ENTRYPOINT ["/usr/local/bin/spring-voyage-agent-sidecar"]
```

The SEA binaries are built from the same source as the npm package and the path-1 base image; all three are versioned in lockstep by the `agent-base-vX.Y.Z` release workflow.

---

## Path 3 — native A2A server

Pick this path when your image already speaks A2A 0.3.x natively. There is no bridge; your process binds `:8999` directly and answers `/.well-known/agent.json` and `POST /`.

The Python `dapr-agents` image at `agents/dapr-agent/` is the in-tree example. The relevant shape:

```dockerfile
# syntax=docker/dockerfile:1.7
FROM python:3.12-slim

WORKDIR /app
COPY requirements.txt .
RUN pip install --no-cache-dir -r requirements.txt

COPY agent.py .

EXPOSE 8999
ENTRYPOINT ["python", "agent.py"]
```

Inside `agent.py` the agent constructs an `A2AStarletteApplication`, registers the JSON-RPC verbs (`message/send`, `tasks/cancel`, `tasks/get`), and serves it over uvicorn on `0.0.0.0:8999`. The Agent Card returned at `GET /.well-known/agent.json` carries `protocolVersion: "0.3"` and the `version` field your image advertises. There is no `SPRING_AGENT_ARGV` involvement on path 3 because the in-container process *is* the agent — nothing is spawned per turn.

The corresponding launcher (`DaprAgentLauncher`) returns an `AgentLaunchSpec` with `Argv` left to the image's ENTRYPOINT and `A2APort = 8999`; the dispatcher dials the running A2A server directly.

---

## Environment contract

Whichever path you pick, the dispatcher (and the launcher you configure for your tool) sets a baseline of env vars inside the container. The launcher composes them into `AgentLaunchSpec.EnvironmentVariables`; the dispatcher merges them with its own baseline before `IContainerRuntime.StartAsync`.

### `SPRING_AGENT_ARGV` (paths 1 and 2 only)

A **JSON-encoded array of strings** the bridge spawns on every `message/send`. The bridge `JSON.parse`s it and execs the result as `argv[0]` with `argv[1..]` — no shell, no string-splitting, no `eval`. The JSON-array shape is non-negotiable: it's the only way to express argv elements that contain whitespace, backslashes, or quotes without ambiguity (see [#1063](https://github.com/cvoya-com/spring-voyage/issues/1063) for the original burn).

Examples:

```bash
SPRING_AGENT_ARGV='["claude","--print","--dangerously-skip-permissions","--output-format","stream-json"]'
SPRING_AGENT_ARGV='["python","-m","my_agent","--mode","interactive"]'
SPRING_AGENT_ARGV='["true"]'   # smoke / no-op (the bridge boots, exposes /healthz, never invokes anything real)
```

Failing to set `SPRING_AGENT_ARGV` is an error on `message/send` (the bridge has nothing to spawn). Setting it as a string-typed env (`SPRING_AGENT_ARGV='claude --print …'`) is also an error — the bridge requires the JSON array shape.

Path 3 (native A2A) ignores `SPRING_AGENT_ARGV` because there's no bridge to read it.

### `SPRING_MCP_ENDPOINT` and `SPRING_AGENT_TOKEN`

The MCP server endpoint and a per-invocation bearer token. Your agent process (whatever the bridge spawns, or your native A2A server) uses these to call back into the platform's MCP surface to discover tools (`tools/list`) and invoke them (`tools/call`). The token is short-lived and revoked in the dispatcher's `finally` block for ephemeral turns; persistent agents reuse a stable session id.

```bash
SPRING_MCP_ENDPOINT=http://host.docker.internal:5050/mcp
SPRING_AGENT_TOKEN=<opaque-bearer-token>
```

### `SPRING_AGENT_ID`, `SPRING_CONVERSATION_ID`, `SPRING_SYSTEM_PROMPT`

Identity and prompt context. Used by launchers that need them in the spawned tool's environment (e.g. for logging, prompt assembly, or memory keying). Not all launchers stamp all of them; consult your launcher's source for the exact set.

### Tool-specific env

Anything else the launcher chooses to stamp — API keys, provider hints, model IDs — flows through unchanged. The bridge has no special knowledge of any specific tool; whatever env you set in `AgentLaunchSpec.EnvironmentVariables` reaches the spawned process unchanged. (Path 3 receives the same env directly.)

---

## The `agent.json` shape

The Agent Card at `GET /.well-known/agent.json` is a JSON object describing the agent. The dispatcher uses two fields from it:

- `protocolVersion` — must be `"0.3"`. The dispatcher refuses to send messages to an agent advertising a different major.
- `version` — the bridge version (paths 1/2) or the native A2A server version (path 3). Logged by the dispatcher; surfaced as version skew when it disagrees with the dispatcher's expected `x-spring-voyage-bridge-version` header.

A complete minimal Agent Card from the bridge:

```json
{
  "name": "Spring Voyage CLI Agent",
  "description": "A2A bridge over a stdin/stdout CLI",
  "version": "1.0.0",
  "protocolVersion": "0.3",
  "url": "http://0.0.0.0:8999/",
  "skills": [],
  "defaultInputModes": ["text"],
  "defaultOutputModes": ["text"]
}
```

A native A2A server (path 3) typically populates `skills[]` with the tools the agent advertises — Spring Voyage doesn't read the field today, but the A2A SDKs surface it to other A2A-aware callers.

---

## Version compatibility

| Surface | Versioning | Compatibility window |
|---------|------------|----------------------|
| **A2A protocol** | Pinned to `0.3.x`. Bumping to `0.4.x` or `1.x` is a deliberate breaking change with a deprecation window on the dispatcher side. | The dispatcher refuses agents advertising a different major. |
| **Bridge (npm + OCI + SEA)** | Semver. Same version published across all three artifacts in lockstep by `release-agent-base.yml`. | The dispatcher accepts bridges within the last **2 majors** (N-2). Older bridges still answer requests, but the dispatcher logs version skew. |
| **`SPRING_*` env contract** | Additive. New env keys land as optional; the bridge ignores keys it doesn't understand. | Operator images that don't set new optional keys keep working. |

When the bridge stamps a different version than the dispatcher expects, the dispatcher logs a single warning per agent at startup and continues. To check the running bridge version manually:

```bash
curl -fsS -i http://localhost:8999/healthz | grep -i x-spring-voyage-bridge-version
```

---

## Debugging tips

### Where to find bridge logs

Paths 1 and 2: the bridge writes to the container's combined stdout/stderr. Read them through whichever surface your runtime exposes:

```bash
# OSS (Podman / Docker on the dispatcher's host)
docker logs <container-id>
podman logs <container-id>

# spring CLI (persistent agents)
spring agent logs <agent-id> --tail 200
```

The bridge's log lines are prefixed with `[agent-sidecar]`. The spawned tool's stdout/stderr is captured by the bridge and surfaced as A2A artifacts on the response (and as separate `stderr` artifacts when stderr is non-empty).

Path 3: the agent process logs directly to stdout/stderr; the same `docker logs` / `spring agent logs` surface applies.

### Verifying the readiness probe

The dispatcher polls `GET /.well-known/agent.json` until the bridge / A2A server is ready (60 s budget, 200 ms backoff). To verify the same probe locally against a running container:

```bash
# Bring up a smoke instance of any path-1 image (the agent-base bridge with
# a no-op argv — it boots, binds :8999, never invokes anything real).
docker run --rm -d --name byoi-smoke -p 8999:8999 \
  -e SPRING_AGENT_ARGV='["true"]' \
  ghcr.io/cvoya-com/agent-base:1.0.0

# Wait for the agent card.
for i in $(seq 1 30); do
  if curl -fsS http://localhost:8999/.well-known/agent.json >/dev/null 2>&1; then
    echo "ready"
    break
  fi
  sleep 1
done

curl -fsS http://localhost:8999/.well-known/agent.json | jq '.name, .protocolVersion, .version'
docker rm -f byoi-smoke
```

If `agent.json` never returns, the bridge isn't binding `:8999`. Common causes:

- ENTRYPOINT was overridden in your Dockerfile and no longer launches the bridge.
- The container's `AGENT_PORT` is set to something other than `8999` and your launcher didn't update `AgentLaunchSpec.A2APort` to match.
- A network policy (rootless podman with a tight firewall, Kubernetes NetworkPolicy) is blocking the readiness probe.

### `/healthz` vs `/.well-known/agent.json`

The bridge exposes both. The dispatcher uses `/.well-known/agent.json` for readiness because the same endpoint also reports the protocol version and bridge version. `/healthz` is a smaller surface useful for orchestrator liveness probes:

```bash
curl -fsS http://localhost:8999/healthz
# {"status":"ok","bridgeVersion":"1.0.0"}
```

### A2A `message/send` from the command line

Once the agent card is reachable, you can fire a single `message/send` against the bridge to verify the spawn path end to end:

```bash
curl -fsS -X POST http://localhost:8999/ \
  -H 'Content-Type: application/json' \
  -d '{
    "jsonrpc": "2.0",
    "id": 1,
    "method": "message/send",
    "params": {
      "message": {
        "role": "user",
        "parts": [{"text": "ping"}]
      },
      "configuration": {
        "acceptedOutputModes": ["text/plain"]
      }
    }
  }' | jq '.result.task.status.state, .result.task.artifacts'
```

A healthy bridge returns `result.task.status.state: "TASK_STATE_COMPLETED"` and a `result.task.artifacts` array carrying whatever the spawned process wrote to stdout. A failure surfaces as `result.task.status.state: "TASK_STATE_FAILED"` with the error in `result.task.status.message` (`role: "ROLE_AGENT"`).

> **Why proto-style enum names?** The `message/send` result is the .NET A2A SDK's [`SendMessageResponse`](https://github.com/a2aproject/a2a-dotnet/blob/main/src/A2A/Models/SendMessageResponse.cs) — a field-presence wrapper around either `task` (an [`AgentTask`](https://github.com/a2aproject/a2a-dotnet/blob/main/src/A2A/Models/AgentTask.cs)) or `message` (a [`Message`](https://github.com/a2aproject/a2a-dotnet/blob/main/src/A2A/Models/Message.cs)). The SDK pins every enum (`TaskState`, `Role`, …) to the proto-style names via `[JsonStringEnumMemberName]` (see [`TaskState.cs`](https://github.com/a2aproject/a2a-dotnet/blob/main/src/A2A/Models/TaskState.cs) and [`Role.cs`](https://github.com/a2aproject/a2a-dotnet/blob/main/src/A2A/Models/Role.cs)) and rejects the lowercase A2A 0.3 spec form with a `JsonException`. The bridge picks the .NET-side casing because the SDK is the wire-stable consumer; tracked in [#1115](https://github.com/cvoya-com/spring-voyage/issues/1115).
>
> The dispatcher's `tasks/get` and `tasks/cancel` calls deserialize the result as a bare `AgentTask` (no `task` wrapper), since `A2AClient.GetTaskAsync` and `CancelTaskAsync` are typed `Task<AgentTask>`. Only `message/send` requires the wrapper.

### Common pitfalls

| Symptom | Likely cause |
|---------|--------------|
| Dispatcher logs `Failed to reach /.well-known/agent.json` after 60 s. | ENTRYPOINT isn't the bridge. PID 1 has to be the bridge (paths 1/2) or your A2A server (path 3). |
| `message/send` returns `TASK_STATE_FAILED` immediately on every turn (paths 1/2). | `SPRING_AGENT_ARGV` is missing, mis-encoded (string instead of JSON array), or points at a binary that's not on PATH. |
| Dispatcher logs `bridge version skew: expected 1.x, observed 0.x`. | The agent image is pinning an older bridge than the dispatcher's compatibility window allows. Re-base on a current `agent-base` tag, or bump the npm / SEA binary version. |
| Agent picks up no MCP tools. | `SPRING_MCP_ENDPOINT` is unreachable from inside the container. On Linux + Podman you typically need `--add-host host.docker.internal:host-gateway`; the dispatcher already adds this to ephemeral configs, but a custom path-3 image must honour the env even if the network setup differs. |
| Persistent agent restarts every few seconds. | The `PersistentAgentRegistry` health monitor is flagging `/.well-known/agent.json` as unhealthy. Read the dispatcher's logs for the failed probe response. A misconfigured proxy (returning HTML instead of JSON) is a common cause. |

---

## Local end-to-end smoke

Spring Voyage ships a smoke driver that exercises the unified dispatch path against the in-tree path-1 (Claude Code) and path-3 (Dapr Agent) images. To run it locally:

```bash
deployment/build-agent-images.sh --tag dev   # build agent-base + claude + dapr at :dev
SMOKE_IMAGE_TAG=dev tests/scripts/smoke-agent-images.sh
```

The script publishes each image on a random host port, waits for `/.well-known/agent.json`, and asserts the minimum Agent Card shape. The CI job `Agent images build + smoke` runs the same steps on every PR that touches the deployment surface.

### `smoke-1087.sh` — full A2A round-trip across both bridge-bearing paths

`tests/scripts/smoke-1087.sh` is the wire-level conformance smoke for the unified dispatch path. It boots an agent image, polls `/.well-known/agent.json`, fires an A2A `message/send`, and asserts a real response (`result.task.status.state == "TASK_STATE_COMPLETED"`, prompt echoed back via the bridge spawning `cat`). The proto-style enum names follow the .NET A2A SDK contract pinned via `[JsonStringEnumMemberName]`; see issue #1115 for the rationale. It covers both bridge-bearing conformance paths from ADR 0027:

```bash
# Path 1 only (default; what CI ran on every PR before #1120).
bash tests/scripts/smoke-1087.sh
bash tests/scripts/smoke-1087.sh --path 1

# Path 2 only — builds tests/fixtures/byoi-path2/Dockerfile against a
# fresh `npm pack` tarball of deployment/agent-sidecar/ and asserts
# the same A2A round-trip. Requires Node + npm on the host (the
# tarball is produced ahead of `docker build`).
bash tests/scripts/smoke-1087.sh --path 2

# Both paths in one run.
bash tests/scripts/smoke-1087.sh --path all
```

The CI job `Agent images build + smoke` runs `--path all` on every PR that touches `deployment/Dockerfile.agent-*`, `deployment/agent-sidecar/**`, `agents/dapr-agent/**`, `tests/scripts/smoke-1087.sh`, or `tests/fixtures/byoi-path2/**` — so a sidecar source change, a Dockerfile change, or a smoke-script change exercises both BYOI conformance paths before merge. Path 3 (native A2A) stays gated behind `SMOKE_DAPR=1` pending [#1110](https://github.com/cvoya-com/spring-voyage/issues/1110).

For the full ephemeral-dispatch round-trip (`StartAsync → readiness → A2A → ReleaseAsync`), the `EphemeralDispatchSmokeTests` integration test in `tests/Cvoya.Spring.Integration.Tests/` runs against a real container runtime when you set `SPRING_RUN_DOCKER_SMOKE=1`:

```bash
SPRING_RUN_DOCKER_SMOKE=1 dotnet test tests/Cvoya.Spring.Integration.Tests/Cvoya.Spring.Integration.Tests.csproj \
  --filter "FullyQualifiedName~EphemeralDispatchSmokeTests"
```

---

## See also

- ADR [0025](../../decisions/0025-unified-agent-launch-contract.md) — the unified dispatch path and the launcher contract (`AgentLaunchSpec`).
- ADR [0026](../../decisions/0026-per-agent-container-scope.md) — why containers are per-agent, not per-unit.
- ADR [0027](../../decisions/0027-agent-image-conformance-contract.md) — the canonical statement of the wire contract and the three conformance paths.
- [`docs/architecture/agent-runtime.md`](../../architecture/agent-runtime.md) — full architecture of the dispatcher, the launcher tiers, and the BYOI conformance section that this guide operationalises.
- [`deployment/agent-sidecar/README.md`](../../../deployment/agent-sidecar/README.md) — bridge wire contract reference (response headers, JSON-RPC verbs, env defaults).
- [`tests/scripts/smoke-agent-images.sh`](../../../tests/scripts/smoke-agent-images.sh) — the smoke driver for the in-tree images.
