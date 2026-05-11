# Spring Voyage Agent Sidecar

A small TypeScript bridge that wraps a stdin/stdout-based CLI behind an [A2A 0.3.x](https://a2a-protocol.org/) endpoint. Spring Voyage's `A2AExecutionDispatcher` talks to every agent over A2A — this sidecar is what lets a tool like `claude`, `codex`, or `gemini` participate in that protocol without modifying the tool itself.

This is **path 2** of the BYOI (Bring-Your-Own-Image) conformance contract from [#1087](https://github.com/cvoya-com/spring-voyage/issues/1087): pull the bridge into any base image you control. Path 1 (`FROM ghcr.io/cvoya-com/agent-base`) is the recommended default — it's just this same bridge, pre-installed.

## Wire contract

| Endpoint                          | Method | Purpose                                                        |
| --------------------------------- | ------ | -------------------------------------------------------------- |
| `/.well-known/agent.json`         | GET    | A2A Agent Card. Includes `protocolVersion: "0.3"` and `version`. |
| `/healthz`                        | GET    | Readiness probe. Returns `{status: "ok", bridgeVersion}`.       |
| `/`                               | POST   | A2A JSON-RPC 2.0: `message/send`, `tasks/cancel`, `tasks/get`. |

Every response carries an `x-spring-voyage-bridge-version` header (and field on the Agent Card / task payload). The dispatcher logs version skew so operators can correlate odd behaviour with stale sidecars.

The bridge listens on `AGENT_PORT` (default `8999`), matching `AgentLaunchSpec.A2APort`.

## Configuration

| Env var                  | Default                       | Purpose                                                                                       |
| ------------------------ | ----------------------------- | --------------------------------------------------------------------------------------------- |
| `AGENT_PORT`             | `8999`                        | TCP port the bridge listens on.                                                               |
| `SPRING_AGENT_ARGV`      | (required for `message/send`) | JSON array of strings — the argv vector to spawn on every message. e.g. `["claude","--print"]`. |
| `AGENT_NAME`             | `Spring Voyage CLI Agent`     | Display name for the Agent Card.                                                              |
| `AGENT_CANCEL_GRACE_MS`  | `5000`                        | Milliseconds between SIGTERM and SIGKILL during cancellation.                                 |

Anything else (auth env vars, `SPRING_MCP_ENDPOINT`, `SPRING_AGENT_TOKEN`, etc.) the launcher injects flows through to the spawned CLI unchanged. The bridge has no special knowledge of any specific tool.

We deliberately do **not** accept a single string `AGENT_CMD`/`AGENT_ARGS` pair. [#1063](https://github.com/cvoya-com/spring-voyage/issues/1063) burned us once already with shell-style splitting; the JSON-array shape is unambiguous, matches `IReadOnlyList<string>` on the dispatcher side ([#1093](https://github.com/cvoya-com/spring-voyage/issues/1093)), and avoids the need for a shell in the container.

## Behaviour

On `message/send`:

1. Spawns `argv[0]` with `argv[1..]`, no shell.
2. Pipes the request's text part(s) to the child's stdin and closes stdin.
3. Captures stdout; surfaces it as the task's terminal artifact.
4. Captures stderr; if non-empty, attaches it as a separate `stderr` artifact for diagnostic purposes.

On `tasks/cancel`:

1. Sends `SIGTERM` to the child.
2. After `AGENT_CANCEL_GRACE_MS` ms, escalates to `SIGKILL`.
3. Reports terminal state `canceled`.

On `tasks/get`: returns the cached terminal status of a previously seen task id.

## Local development

```bash
npm install
npm run build
npm test
```

The test suite uses `node:test` + `tsx` (no extra deps). All tests stub the wrapped CLI with `node -e '…'` so the suite runs anywhere Node 22 does.

To run the bridge locally against a stub agent:

```bash
SPRING_AGENT_ARGV='["node","-e","let b=\"\";process.stdin.on(\"data\",c=>b+=c);process.stdin.on(\"end\",()=>process.stdout.write(\"echo:\"+b))"]' \
  AGENT_PORT=8999 \
  npm start

# In another shell:
curl -s -X POST http://localhost:8999/ \
  -H 'Content-Type: application/json' \
  -d '{"jsonrpc":"2.0","method":"message/send","params":{"message":{"parts":[{"text":"ping"}]}},"id":1}'
```

## Docker

The companion `agent-base` image at `ghcr.io/cvoya-com/agent-base:1.0.0` is built from `deployment/Dockerfile.agent-base`. It bundles `tini`, Node 22, and a pre-installed copy of this bridge under `/opt/spring-voyage/sidecar/`. Use it as a base for any custom agent image:

```dockerfile
FROM ghcr.io/cvoya-com/agent-base:1.0.0

# Install your CLI tool of choice
RUN npm install -g @anthropic-ai/claude-code

# That's it. The base image's ENTRYPOINT runs the sidecar; the dispatcher
# sets SPRING_AGENT_ARGV at launch time.
```

If you can't `FROM` the base image (you need a non-Debian distro, you ship a rootless image, etc.), pull the bridge into your own image with either:

- the npm package: `npm install -g @cvoya/spring-voyage-agent-sidecar` and run `spring-voyage-agent-sidecar` as your entrypoint;
- the Node single-executable binary attached to each GitHub Release: `linux-amd64`, `linux-arm64`, `darwin-arm64`. No Node runtime required.

## Versioning

- Semver on the npm package (`@cvoya/spring-voyage-agent-sidecar`) and the OCI tag (`ghcr.io/cvoya-com/agent-base`).
- N-2 backward compatibility: a Spring Voyage worker dialing this bridge will accept bridge versions within the last 2 major releases.
- A2A protocol pinned to `0.3.x`. Any bump to 0.4 or 1.0 is a deliberate breaking change with a deprecation window on the dispatcher side. The protocol version is on the Agent Card so the dispatcher can log skew.

## Architecture refs

- BYOI conformance contract: [`docs/architecture/agent-runtime.md` § 7](../../docs/architecture/agent-runtime.md#7-byoi-conformance-contract).
- Operator step-by-step (with Dockerfile snippets, env contract, debugging tips): [`docs/guide/byoi-agent-images.md`](../../docs/guide/byoi-agent-images.md).
- Workflow / dispatch sequence: [`docs/architecture/workflows.md`](../../docs/architecture/workflows.md).
- ADRs: [0025 — unified agent launch contract](../../docs/decisions/0025-unified-agent-launch-contract.md), [0026 — per-agent container scope](../../docs/decisions/0026-per-agent-container-scope.md), [0027 — agent-image conformance contract](../../docs/decisions/0027-agent-image-conformance-contract.md).

## License

Business Source License 1.1 — see `/LICENSE.md`.
