# 0058 — Spring Voyage container contract — platform-managed artefacts inside agent instances

- **Status:** Proposed — consolidates the Spring Voyage container contract as a single canonical record. Defines, in one place, every platform-managed artefact an agent container instance carries: the environment-variable surface, the workspace folder and file layout (with a `.spring/` namespace for future platform files), the supervised process tree, the inbound and outbound network endpoints, the sidecar's A2A web-API surface, and the rule for how new contract items get added or deprecated without drift. Absorbs ADR-0055 §7 (layering rule) and §9 (env-var enumeration); cross-references ADR-0055 §5 (workspace mount path) and §6 (sidecar verification) which remain hosted there as load-bearing parts of the bootstrap-mechanism narrative. Does not re-decide any sub-system covered by ADR-0027 (image conformance), ADR-0029 (tenant execution boundary), ADR-0041 (per-thread session resume), ADR-0054 (one MCP server, one execution host), ADR-0055 (pull-based bootstrap), or ADR-0057 (sidecar-local MCP server); it pulls their container-side outputs into one document so future amendments to the container surface land here.
- **Date:** 2026-05-23
- **Related ADRs:** [0027](0027-agent-image-conformance-contract.md) — A2A 0.3.x image conformance contract; defines the inbound `:8999` surface this record catalogues. [0029](0029-tenant-execution-boundary.md) — tenant execution boundary; defines `IAgentContext` and the per-agent workspace volume. [0038](0038-agent-runtime-and-model-provider-split.md) — `(runtime, model)` split; the catalogue this record's runtime-credential env vars are derived from. [0041](0041-actor-runtime-contract.md) — per-thread session resume and the `concurrent_threads` author contract; explains the CLI session-storage env vars (`CLAUDE_CONFIG_DIR`, `GEMINI_CLI_HOME`) this record enumerates. [0052 (archived)](archive/0052-execution-host-roles-and-single-mcp-server.md) — explicit host roles and the worker-only `McpServer`; rolled forward into [0054](0054-one-mcp-server-one-execution-host.md). [0054](0054-one-mcp-server-one-execution-host.md) — one platform MCP server in the worker; the per-turn MCP token contract this record references. [0055](0055-pull-based-agent-bootstrap.md) — pull-based agent bootstrap; this record hosts §7 (layering) and §9 (env vars), cross-references §5 (mount path) and §6 (sidecar verification). [0057](0057-sidecar-local-mcp-server.md) — sidecar-local MCP server (stdio); the spawned-MCP-server-mode child this record's process tree describes.
- **Related docs:** [`docs/architecture/agent-runtime.md`](../architecture/agent-runtime.md) — the architecture-level "how a runtime is configured, launched, and driven for a turn" page; this ADR is the contract-level record sitting under it.
- **Related code:** `src/Cvoya.Spring.Core/Execution/AgentWorkspaceContract.cs`, `src/Cvoya.Spring.Dapr/Execution/AgentContextBuilder.cs`, `src/Cvoya.Spring.AgentRuntimes/Launchers/{ClaudeCodeLauncher,CodexLauncher,GeminiLauncher,SpringVoyageAgentLauncher,LauncherCallbackEnvironment,LauncherOtelEnvironment}.cs`, `src/Cvoya.Spring.AgentSidecar/src/{cli.ts,server.ts,a2a.ts,bridge.ts,bootstrap.ts,mcp-server.ts,mcp-token-store.ts}`.
- **Related issues:** [#2697](https://github.com/cvoya-com/spring-voyage/issues/2697) — this ADR. [#2667](https://github.com/cvoya-com/spring-voyage/issues/2667) — umbrella under which the contract questions surfaced. [#2672](https://github.com/cvoya-com/spring-voyage/issues/2672) — the `.spring/` namespace question this record answers. [#2695](https://github.com/cvoya-com/spring-voyage/issues/2695) — the launcher work that depends on the namespace decision. [#2668](https://github.com/cvoya-com/spring-voyage/issues/2668) — parallel PR removing `SPRING_SYSTEM_PROMPT` from the three CLI launchers; this record's env-var table reflects the post-#2668 target state.

## Context

The Spring Voyage agent container is one of the few platform surfaces that
crosses every boundary the system has: the tenant trust boundary
([ADR-0029](0029-tenant-execution-boundary.md)), the dispatcher / worker
boundary ([ADR-0012](0012-spring-dispatcher-service-extraction.md)), the
in-container process tree ([ADR-0027](0027-agent-image-conformance-contract.md)),
the per-agent workspace volume ([ADR-0055 §5](0055-pull-based-agent-bootstrap.md)),
the per-turn MCP session ([ADR-0054 §5](0054-one-mcp-server-one-execution-host.md)),
the sidecar-local MCP server ([ADR-0057](0057-sidecar-local-mcp-server.md)),
and the runtime catalogue's per-edge credential mapping
([ADR-0038](0038-agent-runtime-and-model-provider-split.md)). Each of those
ADRs decided one slice of the container's contents. None of them enumerates the
full set, and the container contract today is read by composing fragments from
seven ADRs plus the implementation in `AgentContextBuilder`, four launchers,
and the sidecar.

The cost has been visible in v0.1 review traffic. Each sub-issue of the
container-cleanup umbrella ([#2667](https://github.com/cvoya-com/spring-voyage/issues/2667))
surfaces a small naming or layout question that gets decided ad-hoc:

- [#2672](https://github.com/cvoya-com/spring-voyage/issues/2672) asks where
  the platform's assembled system prompt should live when it stops being
  written to Claude Code's auto-discovered `CLAUDE.md` (and whether to
  introduce a `.spring/` namespace at the workspace root).
- [#2668](https://github.com/cvoya-com/spring-voyage/issues/2668) removes a
  dead env var (`SPRING_SYSTEM_PROMPT`) from the three CLI launchers because
  no consumer reads it — discovered only by grepping.
- [#2697](https://github.com/cvoya-com/spring-voyage/issues/2697) (this ADR's
  issue) names the underlying gap: every contract item is decided somewhere,
  but the contract has no canonical statement.

There is a structural drift risk too. The platform writes files into the
container at three points in time (bootstrap fetch, A2A `message/send`
metadata, per-turn integrity check) and reads env vars from at least three
producers (launcher, dispatcher, sidecar). Without a single catalogued
surface, an "and one more" addition can ship under any one of those producers
without an explicit decision about where it should live, who owns it, and how
long it lives. The dead `SPRING_SYSTEM_PROMPT` is the canonical instance: it
was emitted by three launchers, consumed by nobody, and survived multiple
refactors because no document said it had to.

This ADR is the canonical contract. It does not re-decide any individual
contract item — the prior ADRs each settled their respective decisions and
those decisions stand. It pulls the *outputs* into one record so:

1. Future amendments to the container contract land here, not in whichever
   ADR happens to touch the same surface.
2. New contract items have a single template to fit (env vars get a row in
   §1's table; new files get a `.spring/`-namespaced path per §2; new
   inbound/outbound network endpoints get a row in §4).
3. Dead contract items can be discovered by reading one document and
   matching it against the implementation.

## Decision

The agent container's platform-managed surface is the union of the six
sections below. Anything not enumerated here is **not** a platform contract
item — it is either a runtime-author concern (under the workspace's repo
layer, §2) or a platform-internal seam not visible inside the container.

### 1. Environment variables

Every `SPRING_*` env var, every CLI-credential env var, and every CLI-storage
env var the platform stamps onto the agent container. The table records each
variable's **producer** (the platform component that writes the value), its
**lifetime** (how long the value is valid), and the **consumer** (the
in-container process that reads it). For env vars whose value is sensitive
(tokens, API keys), the value column says "secret" rather than reproducing the
shape; the credential resolution path lives on
[ADR-0038](0038-agent-runtime-and-model-provider-split.md) §4.

The producer column uses three names:

- **AgentContextBuilder** — `src/Cvoya.Spring.Dapr/Execution/AgentContextBuilder.cs`,
  the worker-side service that builds the env-var dictionary on every launch
  ([ADR-0029](0029-tenant-execution-boundary.md)).
- **Launcher** — the per-runtime `IAgentRuntimeLauncher` strategy under
  `src/Cvoya.Spring.AgentRuntimes/Launchers/`.
- **Dispatcher (per-turn)** — the per-turn A2A `message/send` metadata, not
  an env var; included where a long-lived env var carries the default and a
  per-turn override rides A2A metadata instead.

| Env var | Producer | Lifetime | Consumer | Notes |
|---|---|---|---|---|
| `SPRING_TENANT_ID` | AgentContextBuilder | Agent lifetime | All in-container processes | Canonical tenant id (Guid). |
| `SPRING_AGENT_ID` | AgentContextBuilder | Agent lifetime | All in-container processes | Canonical agent / member id. Also names the per-member workspace mount under `/spring/members/<id>/` (§2). |
| `SPRING_UNIT_ID` | AgentContextBuilder | Agent lifetime | All in-container processes | Optional; absent for standalone agents. |
| `SPRING_THREAD_ID` | Launcher (per-turn for ephemeral; absent on persistent supervisor restarts) | Per-turn (ephemeral) or agent-lifetime (persistent) | CLI runtimes (Claude Code, Codex, Gemini): bridge appends `--session-id/--resume <id>` ([ADR-0041](0041-actor-runtime-contract.md)). Spring Voyage agent: reads directly. | The platform thread id IS the runtime session id; see ADR-0041. |
| `SPRING_WORKSPACE_PATH` | AgentContextBuilder + Launcher (redundantly) | Agent lifetime | Sidecar (bridge, MCP-server-mode, bootstrap fetcher), every CLI's per-turn spawn cwd | Per-member workspace mount path (§2.1). Always `/spring/members/<agentId>/`. |
| `SPRING_BOOTSTRAP_URL` | AgentContextBuilder | Agent lifetime | Sidecar (bootstrap fetcher) | Worker-hosted bootstrap endpoint URL ([ADR-0055 §9](0055-pull-based-agent-bootstrap.md)). |
| `SPRING_BOOTSTRAP_TOKEN` | AgentContextBuilder | Agent lifetime (revoked on undeploy) | Sidecar (bootstrap fetcher) | Secret. Per-agent bearer ([ADR-0055 §8](0055-pull-based-agent-bootstrap.md)). |
| `SPRING_MCP_URL` | AgentContextBuilder | Agent lifetime | Sidecar (MCP-server-mode child) | Worker `POST /mcp/` endpoint. Sidecar dials it on the CLI's behalf ([ADR-0057 §2](0057-sidecar-local-mcp-server.md)). Also stamped into the `.mcp.json` env block under `SPRING_MCP_PROXY_URL` per launcher (see below). |
| `SPRING_MCP_TOKEN` | AgentContextBuilder | Per-turn (overridden per turn by A2A `message/send.mcpToken` metadata) | Sidecar | Secret. Per-turn MCP session token ([ADR-0054 §5](0054-one-mcp-server-one-execution-host.md)). The launch-time value is empty on a freshly-deployed persistent agent; the first dispatched turn carries the real token in A2A metadata, which the sidecar writes to a workspace token file ([§2.3](#23-the-workspace-resident-token-file) below). |
| `SPRING_CONCURRENT_THREADS` | AgentContextBuilder | Agent lifetime | Sidecar, runtime SDKs | `"true"` or `"false"`. The `concurrent_threads` policy from the agent's YAML ([ADR-0041](0041-actor-runtime-contract.md)). |
| `SPRING_BUCKET2_URL` | AgentContextBuilder (optional) | Agent lifetime | Spring Voyage AgentSDK runtimes | Public Web API endpoint ([ADR-0029](0029-tenant-execution-boundary.md) Bucket 2). |
| `SPRING_BUCKET2_TOKEN` | AgentContextBuilder | Agent lifetime | Spring Voyage AgentSDK runtimes | Secret. |
| `SPRING_LLM_PROVIDER_URL` | AgentContextBuilder | Agent lifetime | Spring Voyage AgentSDK runtimes (Ollama et al.) | Platform-hosted LLM endpoint. |
| `SPRING_LLM_PROVIDER_TOKEN` | AgentContextBuilder | Agent lifetime | Spring Voyage AgentSDK runtimes | Secret. |
| `SPRING_LLM_PROVIDER` | `SpringVoyageAgentLauncher` | Agent lifetime | `spring-voyage` runtime | Provider id (`anthropic` / `openai` / `google` / `ollama`). |
| `SPRING_LLM_COMPONENT` | `SpringVoyageAgentLauncher` | Agent lifetime | `spring-voyage` runtime | Dapr Conversation component name (`llm-<provider>`). |
| `SPRING_MODEL` | `SpringVoyageAgentLauncher` | Agent lifetime | `spring-voyage` runtime | Model id. |
| `SPRING_TELEMETRY_URL` | AgentContextBuilder (optional) | Agent lifetime | Spring Voyage AgentSDK runtimes | OTLP endpoint (legacy; OTLP runtimes prefer `OTEL_EXPORTER_OTLP_ENDPOINT`, below). |
| `SPRING_TELEMETRY_TOKEN` | AgentContextBuilder (optional) | Agent lifetime | Spring Voyage AgentSDK runtimes | Secret. |
| `SPRING_CALLBACK_URL` | AgentContextBuilder (callback env builder) | Agent lifetime | Spring Voyage AgentSDK runtimes; `LauncherOtelEnvironment` (rewrites to OTLP endpoint) | OTLP / SDK callback base URL. Distinct trust plane from MCP. |
| `SPRING_CALLBACK_TOKEN` | AgentContextBuilder | Per-invocation (launch-time default; per-message override on A2A `message/send.callbackToken` metadata for persistent containers) | Spring Voyage AgentSDK runtimes; OTLP exporter | Secret. JWT used by OTLP ingest auth and the AgentSDK's callback path. |
| `SPRING_AGENT_ARGV` | Launcher (CLI runtimes) | Per-turn | Sidecar (`bridge.ts`, parsed via `JSON.parse`) | JSON-encoded argv array the bridge exec's on every `message/send`. Encoding-as-JSON avoids shell splitting ([#1063 history](https://github.com/cvoya-com/spring-voyage/issues/1063)). |
| `SPRING_THREAD_ID_ARG_CREATE` | Launcher (CLI runtimes with `--session-id` flow) | Agent lifetime | Sidecar (`config.ts: parseThreadBinding`) | CLI flag used to *create* a fresh session with a supplied id (Claude Code: `--session-id`; Gemini: `--session-id`). |
| `SPRING_THREAD_ID_ARG_RESUME` | Launcher | Agent lifetime | Sidecar (`config.ts: parseThreadBinding`) | CLI flag used to *resume* a session by id (Claude Code: `--resume`; Gemini: `--resume`). |
| `SPRING_TOOLS_MANIFEST` | Image (declared in Dockerfile) | Image lifetime | Sidecar (`server.ts`, `GET /a2a/tools`) | Absolute path to a JSON file enumerating the image's tool surface. Read on every `/a2a/tools` call so it can be hot-swapped in tests. |
| `AGENT_PORT` / `DAPR_AGENT_PORT` | `SpringVoyageAgentLauncher` | Agent lifetime | `spring-voyage` runtime | A2A port the in-container A2A server binds to. Default `8999`. |
| `DAPR_API_TIMEOUT_SECONDS` | `SpringVoyageAgentLauncher` | Agent lifetime | `spring-voyage` runtime's Dapr Conversation Alpha2 unary call | `"600"` — overrides the Dapr SDK's 60 s default so slow CPU-bound Ollama turns do not hit `DEADLINE_EXCEEDED`. |
| `CLAUDE_CODE_OAUTH_TOKEN` | Launcher (`ClaudeCodeLauncher`) | Agent lifetime | Claude Code CLI | Secret. Anthropic OAuth token (`sk-ant-oat…`). Resolved per-edge via the catalogue ([ADR-0038 §4](0038-agent-runtime-and-model-provider-split.md)). |
| `OPENAI_API_KEY` | Launcher (`CodexLauncher`, `SpringVoyageAgentLauncher` for `provider: openai`) | Agent lifetime | Codex CLI; `spring-voyage` runtime via Dapr Conversation local-env secret store | Secret. |
| `GOOGLE_API_KEY` | Launcher (`GeminiLauncher`, `SpringVoyageAgentLauncher` for `provider: google`) | Agent lifetime | Gemini CLI; `spring-voyage` runtime | Secret. |
| `ANTHROPIC_API_KEY` | Launcher (`SpringVoyageAgentLauncher` for `provider: anthropic`) | Agent lifetime | `spring-voyage` runtime via Dapr Conversation local-env secret store | Secret. Note: distinct from `CLAUDE_CODE_OAUTH_TOKEN` — the Claude Code CLI is OAuth-only ([ADR-0038 §4](0038-agent-runtime-and-model-provider-split.md)). |
| `CLAUDE_CONFIG_DIR` | Launcher (`ClaudeCodeLauncher`) | Agent lifetime | Claude Code CLI | Per-member workspace-relative path (`$SPRING_WORKSPACE_PATH/.claude/`). Anchors Claude Code's session-file storage on the per-agent workspace volume so `--resume` works across restarts ([ADR-0041](0041-actor-runtime-contract.md)). |
| `GEMINI_CLI_HOME` | Launcher (`GeminiLauncher`) | Agent lifetime | Gemini CLI | Per-member workspace mount root. Gemini CLI appends `.gemini/` to it for session-file storage ([ADR-0041](0041-actor-runtime-contract.md)). |
| `OTEL_EXPORTER_OTLP_ENDPOINT` | `LauncherOtelEnvironment` | Agent lifetime | OTLP-exporting runtimes | Derived from `SPRING_CALLBACK_URL` + `/otlp` ([#2492](https://github.com/cvoya-com/spring-voyage/issues/2492)). |
| `OTEL_EXPORTER_OTLP_PROTOCOL` | `LauncherOtelEnvironment` | Agent lifetime | OTLP-exporting runtimes | Default `http/protobuf` ([#2501](https://github.com/cvoya-com/spring-voyage/issues/2501)). |
| `OTEL_EXPORTER_OTLP_HEADERS` | `LauncherOtelEnvironment` | Agent lifetime | OTLP-exporting runtimes | `Authorization=Bearer <SPRING_CALLBACK_TOKEN>`. |
| `OTEL_RESOURCE_ATTRIBUTES` | `LauncherOtelEnvironment` | Agent lifetime | OTLP-exporting runtimes | `sv.tenant.id`, `sv.subject.uuid`, `sv.subject.kind` resource tags the ingest controller cross-checks against the bearer's claims. |
| `OTEL_SERVICE_NAME` | `LauncherOtelEnvironment` | Agent lifetime | OTLP-exporting runtimes | `spring-voyage/<subject-kind>`. |

#### 1.1 Env vars that are NOT part of the contract

The following env vars have been emitted by older versions or by parallel
work in flight; this record names them so they cannot creep back in without
an explicit amendment:

- `SPRING_SYSTEM_PROMPT` — emitted by the CLI launchers, consumed by no
  in-container process (the CLI reads the prompt from `CLAUDE.md` /
  `AGENTS.md` / `GEMINI.md` it auto-discovers under `$SPRING_WORKSPACE_PATH`).
  Removed from the three CLI launchers by [#2668](https://github.com/cvoya-com/spring-voyage/issues/2668);
  retained on `SpringVoyageAgentLauncher` where the in-container Python agent
  consumes it directly.
- `SPRING_ORCHESTRATION_MCP_CONFIG` — never set; the per-turn `.mcp.json`
  refresh path that depended on it was removed ([ADR-0052 archived](archive/0052-execution-host-roles-and-single-mcp-server.md)).
- `SPRING_MCP_ENDPOINT`, `SPRING_AGENT_TOKEN` — early names superseded by
  `SPRING_MCP_URL` / `SPRING_MCP_TOKEN` (`#1322`). The current names are the
  contract; the older names are not.

### 2. Workspace folder and file layout

#### 2.1 Mount path

The workspace volume is always mounted at `/spring/members/<memberId>/`
including the single-member "standalone" case. The canonical statement
remains [ADR-0055 §5](0055-pull-based-agent-bootstrap.md) and the helper
`AgentWorkspaceContract.BuildMountPath(memberId)`; this record cross-references
that decision rather than duplicating it, because the mount path is
load-bearing to ADR-0055's bootstrap-mechanism narrative.

The `<memberId>` segment is the agent / unit / human's canonical Guid id
([ADR-0036](0036-single-identity-model.md)).

#### 2.2 Namespace decision

The workspace root carries three classes of platform-owned entries:

1. **CLI auto-discovery files**, owned by the CLI's own conventions, sit
   **at the workspace root**:
   - `CLAUDE.md` — Claude Code's per-project instructions file.
   - `AGENTS.md` — Codex's per-project instructions file.
   - `GEMINI.md` — Gemini CLI's per-project instructions file.
   - `.mcp.json` — Claude Code's per-project MCP server discovery file.
   - `.claude/` — Claude Code's per-project session storage and config
     (pointed at by `CLAUDE_CONFIG_DIR`).
   - `.gemini/` — Gemini CLI's per-project settings directory (containing
     `settings.json`); also pointed at indirectly by `GEMINI_CLI_HOME`.

   These names are dictated by the CLIs we wrap. We do not own the names; we
   own the contents. They cannot move into a Spring-namespaced subtree
   without losing the CLI integration. The set is closed by §6's
   addition-rule — a new CLI we wrap may add more root entries with an
   amendment; ad-hoc additions are not.

2. **Spring-namespaced platform files**, under `.spring/` at the workspace
   root, are the namespace for **new** platform-managed files where the
   platform chooses the name:
   - Workspace-relative path: `$SPRING_WORKSPACE_PATH/.spring/`
     = `/spring/members/<memberId>/.spring/`.
   - Owner: the platform (sidecar writes; integrity check pins).
   - Examples (current and proposed):
     - The platform's assembled system prompt, when it stops being written
       to Claude Code's auto-discovered `CLAUDE.md` (proposed in
       [#2672](https://github.com/cvoya-com/spring-voyage/issues/2672)). The
       path becomes `.spring/system-prompt.md`.
     - The sidecar's per-turn MCP token file (currently at
       `$SPRING_WORKSPACE_PATH/.spring-voyage-bridge/mcp-token`,
       written by `mcp-token-store.ts`) is the long-running platform's
       working directory. A v0.2 migration to `.spring/bridge/mcp-token`
       is in scope as a tracked follow-up but **not** in scope for this
       ADR; the current path is grandfathered.

   The `.spring/` namespace exists so platform-managed files do not collide
   with anything in §2.4's repo layer (which lives in arbitrary subpaths of
   the workspace) and stay legible in operator inspection of an agent's
   workspace volume.

3. **Sidecar working directory** (legacy path, in scope to migrate but
   not in this ADR):
   - `$SPRING_WORKSPACE_PATH/.spring-voyage-bridge/` — currently used by
     the sidecar for the per-turn MCP token (`mcp-token`) and the
     thread-id seen-list (`seen-threads`). Folds into `.spring/bridge/` in
     v0.2.

#### 2.3 The workspace-resident token file

The per-turn MCP session token (§1's `SPRING_MCP_TOKEN`) is written to a
known workspace path by the long-running A2A sidecar before each CLI spawn,
and read from that path by the per-turn sidecar-MCP-server-mode child
([ADR-0057 §3](0057-sidecar-local-mcp-server.md)). The current path is
`$SPRING_WORKSPACE_PATH/.spring-voyage-bridge/mcp-token`; under §2.2's v0.2
migration it becomes `$SPRING_WORKSPACE_PATH/.spring/bridge/mcp-token`. The
contract is "the sidecar's two processes agree on the path"; the path itself
is internal to the sidecar.

#### 2.4 Layering rule — platform layer vs repo layer

This statement is the canonical layering rule for the workspace contract; it
moves here from [ADR-0055 §7](0055-pull-based-agent-bootstrap.md), which now
cross-references this section.

The workspace volume holds two distinct kinds of content:

- **Platform layer.** Files at fixed paths under the workspace root, listed
  in the bootstrap bundle's `platformFileHashes`
  ([ADR-0055 §3](0055-pull-based-agent-bootstrap.md)). Owned by the sidecar.
  The integrity check pins them ([§2.5](#25-sidecar-verification-surface)).
  Authoritative regardless of what any clone, agent runtime, or
  in-container process does. This includes:
  - The CLI auto-discovery files §2.2.1 lists (`CLAUDE.md`, `AGENTS.md`,
    `GEMINI.md`, `.mcp.json`, `.gemini/settings.json`).
  - Future `.spring/`-namespaced platform files (§2.2.2).

- **Repo layer.** Anything an agent puts under a cloned repository in its
  workspace (e.g. `<workspace>/myrepo/AGENTS.md`,
  `<workspace>/myrepo/CLAUDE.md`). The CLI may read these per its own rules
  — Claude Code reads `AGENTS.md` and `CLAUDE.md` walking up from `cwd` —
  but the platform does not own them, does not deliver them, does not
  verify them.

**Non-override invariant.** The platform layer is not overridable by the
repo layer. A repo-level `CLAUDE.md` cannot replace the platform `CLAUDE.md`
at the workspace root: the sidecar restores the platform bytes on the next
turn. The CLI's `cwd`-walking discovery of repo-level instruction files runs
*under* the workspace root — repo-level files extend the prompt for that
working directory; they never replace the platform-layer file.

The platform does not assume an agent has a repo. The bundle contract is
"here are the files the platform owns at workspace-root-relative paths".
Whether the agent clones a repo into its workspace is the agent's choice
and concern.

#### 2.5 Sidecar verification surface

Which workspace files the long-running sidecar re-verifies before every CLI
spawn (per [ADR-0055 §6](0055-pull-based-agent-bootstrap.md)) is recorded
on ADR-0055; this section names it and points there.

The surface verified per turn is exactly the set listed in the bootstrap
bundle's `platformFileHashes` — the platform-authoritative subset of the
bundle's `files`. Connector contributions and other bundle entries are not
pinned per turn. The set is data, not code: a launcher contributes a file to
`platformFileHashes` by listing it in its `AgentBootstrapContribution.PlatformFilePaths`
return value.

### 3. Processes inside the container

The agent container runs the following processes. The table marks each
process as **contractual** (the platform requires this shape) or
**incidental** (the deployment may vary it without breaking the contract).

| Process | Status | Source | Lifetime | Notes |
|---|---|---|---|---|
| `tini` (PID 1) | Contractual | Bundled in the agent-base image | Container lifetime | Init / zombie reaper. Required by [ADR-0027](0027-agent-image-conformance-contract.md). |
| `node /opt/.../sidecar/dist/cli.js` (long-running A2A bridge) | Contractual on BYOI paths 1 and 2; absent on path 3 | The agent-base image's ENTRYPOINT (BYOI path 1); installed via SEA binary (BYOI path 2) | Container lifetime | The long-running A2A 0.3.x server on `:8999`. Owns bootstrap fetch, per-turn integrity check, A2A handlers, per-turn MCP-token write. |
| Native A2A server (e.g. `python agent.py`) | Contractual on BYOI path 3; absent on paths 1 and 2 | The image's own ENTRYPOINT / CMD | Container lifetime | Replaces the bridge for runtimes that speak A2A natively (e.g. the `spring-voyage-agent` Python image). |
| CLI runtime (`claude` / `codex` / `gemini`) | Contractual on CLI runtimes | Spawned per A2A `message/send` by the bridge | Per turn | One cold-start spawn per turn; the runtime loads its session from disk via `--resume` ([ADR-0041](0041-actor-runtime-contract.md)). |
| Sidecar MCP-server-mode child (`node /opt/.../cli.js mcp`) | Contractual on CLI runtimes ([ADR-0057](0057-sidecar-local-mcp-server.md)) | Spawned per tool-use round by the CLI itself, named in `.mcp.json` | Per tool-use round | Stdio MCP server that proxies onto the worker's `POST /mcp/`. |
| `daprd` sidecar | **Incidental.** Not in the agent container in the OSS topology ([ADR-0028 D-A](0028-tenant-scoped-runtime-topology.md)). Per-tenant Dapr lives off the tenant network. | — | — | A future Dapr-Conversation-in-container variant for non-Ollama providers would change this; not in scope for v0.1. |
| OTLP exporter | Contractual — embedded in the runtime process, not a separate process. The runtime SDK exports spans/logs to `OTEL_EXPORTER_OTLP_ENDPOINT` ([§1](#1-environment-variables)). No separate OTLP-exporter process runs inside the container. | — | — | Reuses `SPRING_CALLBACK_TOKEN` for OTLP auth ([#2492](https://github.com/cvoya-com/spring-voyage/issues/2492)). |
| `supervisord` | **Not present.** PID-1 supervision is `tini` ([ADR-0027](0027-agent-image-conformance-contract.md)); the long-running sidecar process is the only thing requiring supervision. | — | — | Named here to head off the "shouldn't we add supervisord?" question. The answer is no; if a future image needs to supervise more than one long-running process, that is the trigger to revisit. |

### 4. Network endpoints

#### 4.1 Inbound

The agent container exposes exactly one inbound network endpoint:

| Port | Path | Protocol | Purpose | ADR |
|---|---|---|---|---|
| `8999` (or the launcher's `AgentLaunchSpec.A2APort`) | `POST /` (JSON-RPC), `GET /.well-known/agent.json`, `GET /healthz`, `GET /a2a/tools` | A2A 0.3.x over HTTP/JSON-RPC 2.0 | Per-turn dispatch from the worker's `A2AExecutionDispatcher` | [0027](0027-agent-image-conformance-contract.md) |

No other inbound port. The container does not expose MCP HTTP, does not
expose the OTLP exporter, does not expose Bucket-2. The MCP traffic is
sidecar-local stdio ([ADR-0057](0057-sidecar-local-mcp-server.md)); the
OTLP traffic is outbound; Bucket 2 is outbound.

#### 4.2 Outbound

The agent container's outbound calls, with the env vars that name each
target. All outbound destinations are tenant-network / platform endpoints
plus any upstream the runtime author dials (e.g. the LLM provider for CLI
runtimes that talk directly to Anthropic / OpenAI / Google).

| Destination | Dialler | URL source | Auth | ADR |
|---|---|---|---|---|
| Worker bootstrap endpoint | Sidecar `bootstrap.ts` | `$SPRING_BOOTSTRAP_URL` | `Authorization: Bearer $SPRING_BOOTSTRAP_TOKEN` | [0055](0055-pull-based-agent-bootstrap.md) |
| Worker MCP endpoint (`POST /mcp/`) | Sidecar MCP-server-mode child | `$SPRING_MCP_URL` (also stamped into `.mcp.json` env block as `SPRING_MCP_PROXY_URL`) | `Authorization: Bearer <per-turn MCP session token>` from the workspace token file | [0054](0054-one-mcp-server-one-execution-host.md), [0057](0057-sidecar-local-mcp-server.md) |
| Upstream LLM provider | CLI runtime (Claude Code → Anthropic; Codex → OpenAI; Gemini → Google) **or** the platform's Dapr Conversation REST path (the `spring-voyage` runtime → `llm-<provider>.yaml`) | The CLI's hard-coded URL **or** the Dapr Conversation component | Per-edge credential env var (§1's `CLAUDE_CODE_OAUTH_TOKEN` / `OPENAI_API_KEY` / `GOOGLE_API_KEY` / `ANTHROPIC_API_KEY`) | [0038](0038-agent-runtime-and-model-provider-split.md) |
| OTLP collector | Runtime SDK | `OTEL_EXPORTER_OTLP_ENDPOINT` (derived from `SPRING_CALLBACK_URL`) | `OTEL_EXPORTER_OTLP_HEADERS` carrying `SPRING_CALLBACK_TOKEN` | [#2492](https://github.com/cvoya-com/spring-voyage/issues/2492) |
| Callback endpoint (AgentSDK only) | `Cvoya.Spring.AgentSdk.MessagingClient` | `$SPRING_CALLBACK_URL` | `$SPRING_CALLBACK_TOKEN` (or per-message `callbackToken` metadata override) | [0029](0029-tenant-execution-boundary.md) |
| Connector endpoints | Agent author's tool code (out-of-platform) | Connector-supplied URL (often via `IConnectorRuntimeContextContributor` files in the bootstrap bundle) | Per-connector | [0045](0045-connector-domain-agnostic-platform.md) |
| Public Web API (Bucket 2) | Spring Voyage AgentSDK runtimes | `$SPRING_BUCKET2_URL` | `$SPRING_BUCKET2_TOKEN` | [0029](0029-tenant-execution-boundary.md) |

### 5. Web API: the sidecar's A2A surface

The sidecar's A2A 0.3.x surface is the platform's only contractual web API
**inside** the agent container. It is consumed only by the worker's
`A2AExecutionDispatcher`. The wire shape is ADR-0027's contract; this
section catalogues the routes the sidecar serves and points at ADR-0027
for the underlying protocol.

| Method | Path | Body | Purpose |
|---|---|---|---|
| `GET` | `/.well-known/agent.json` (also `/.well-known/agent-card.json` alias) | — | A2A Agent Card discovery. `protocolVersion: "0.3"`; carries the `x-spring-voyage-bridge-version` field the dispatcher logs for version-skew detection ([ADR-0027](0027-agent-image-conformance-contract.md)). |
| `GET` | `/healthz` | — | Readiness probe. Returns `{ status: "ok", bridgeVersion: <semver> }`. Used by the worker to gate dispatch on a successful first bootstrap fetch. |
| `GET` | `/a2a/tools` | — | Image-tier tool surface. Reads `$SPRING_TOOLS_MANIFEST` on every call ([#2336](https://github.com/cvoya-com/spring-voyage/issues/2336)). Returns `[]` when unset. |
| `POST` | `/` (also empty path) | A2A JSON-RPC 2.0 request | The A2A 0.3.x verbs `message/send`, `tasks/get`, `tasks/cancel`. Per-turn MCP token rides on `message/send.params.message.metadata.mcpToken`; per-message OTLP-callback token override rides on `callbackToken` metadata. |

Every response carries the `x-spring-voyage-bridge-version` header per
[ADR-0027](0027-agent-image-conformance-contract.md). Versioning of the A2A
wire itself is the bridge's semver pinned to A2A 0.3.x; a bump is a
coordinated change across all three BYOI conformance paths.

### 6. Versioning and drift prevention

This section is the rule that keeps §§1–5 from drifting. It applies to
every addition, deprecation, or rename of a platform-managed artefact
inside the agent container.

#### 6.1 Adding a contract item

A new platform-managed env var, workspace file, process, network endpoint,
or web-API route lands as an **amendment to this ADR in the same PR** that
adds the producer/consumer code. The PR:

1. Adds a row to the relevant table in §§1–5.
2. Adds an entry to `AgentWorkspaceContract` (for workspace-path env vars
   or workspace paths) or names the producer constant where the value
   originates.
3. For Spring-managed files: chooses a path under `.spring/` per §2.2.2.
   For env vars: uses the `SPRING_*` prefix unless dictated by an upstream
   convention (`CLAUDE_CODE_OAUTH_TOKEN`, `OTEL_*`, etc.).
4. For inbound endpoints: justifies why the existing `:8999` A2A surface
   does not suffice — the default answer is that a new inbound port is
   rejected (the contract is "one inbound endpoint per agent container").

A new contract item that does **not** amend this ADR is, by definition,
not a contract item. The drift the issue this ADR closes
([#2697](https://github.com/cvoya-com/spring-voyage/issues/2697)) is filed
against was exactly this — items emitted by producers, consumed by
nothing, surviving across refactors because no document made them
contractual.

#### 6.2 Deprecating a contract item

A contract item is deprecated by an amendment to this ADR that:

1. Moves the item's row from the active table to §1.1 / a §-specific
   equivalent ("not part of the contract") with a short rationale.
2. Names the PR that removes the producer.
3. Names the consumer migration (or confirms the item has no consumer,
   the `SPRING_SYSTEM_PROMPT` shape).

The two-step shape — amend ADR + remove code — keeps "is this in the
contract?" answerable from the ADR alone, without grep.

#### 6.3 Schema bumps vs ADR amendments

The contract has no separate schema number; the ADR is the schema. A
breaking change to a contract item (renaming an env var with a consumer;
changing the MCP token's delivery wire; moving a workspace file out of the
CLI's auto-discovery path) lands as an amendment with a "**Breaking
change.**" banner and a clean-deploy directive consistent with v0.1's
hard-rename stance ([ADR-0038 §7](0038-agent-runtime-and-model-provider-split.md)).

A future v0.2 will introduce a schema version field (likely on the
bootstrap bundle's `version` field) so a runtime can negotiate against
the contract version it was built against. v0.1 ships under "the ADR is
the contract, the deployment is fresh on each release".

#### 6.4 Cross-ADR amendments

When a future ADR for a related sub-system (a new MCP transport, a new
BYOI conformance path, a new tenant-execution boundary item) introduces
a container-side artefact, that ADR amends *this* ADR's relevant table
in the same PR, in addition to its own decision content. The amendment
is a one-line row update with a back-link to the originating ADR — the
canonical contract continues to read top-to-bottom from this document.

## Consequences

- **One canonical statement.** Every container-contract item is enumerated
  in one place. "What does the platform stamp into the agent container?"
  has one answerable document.
- **The `.spring/` namespace is decided.** New platform-managed workspace
  files use `.spring/`; CLI auto-discovery files stay at the workspace
  root. This resolves the open question on [#2672](https://github.com/cvoya-com/spring-voyage/issues/2672)
  and unblocks the launcher work on [#2695](https://github.com/cvoya-com/spring-voyage/issues/2695).
- **ADR-0055 §7 (layering) and §9 (env vars) move here.** ADR-0055 now
  contains pointers to this document for those sections; the bootstrap-
  mechanism narrative (§§1–4, §6, §8, §10–11) stays on ADR-0055. The mount
  path (§5) and per-turn integrity check (§6) remain on ADR-0055 because
  they are load-bearing to its narrative; this ADR cross-references them.
- **Dead contract items become discoverable by inspection.** Anything in
  the producer code but not in §§1–5 (and not under §6.2's deprecation
  process) is by definition orphaned and a candidate for deletion. This
  is the property [#2668](https://github.com/cvoya-com/spring-voyage/issues/2668)'s
  cleanup of `SPRING_SYSTEM_PROMPT` relies on.
- **The contract has a single addition shape.** §6.1's "amend this ADR in
  the same PR as the producer/consumer code" rule replaces the implicit
  "ship it under whichever ADR happens to touch this surface" pattern.
  The cost is one additional document edit per contract change; the gain
  is the cost stays at one document.
- **The contract has a single deprecation shape.** §6.2 makes
  "the producer is gone, what's the consumer?" a question the ADR has
  to answer before the removal lands. The dead `SPRING_SYSTEM_PROMPT` is
  the canonical case this prevents from recurring.
- **No new abstractions, no new code.** This ADR ships as a document
  change. The implementations it catalogues stay as they are; the only
  code-adjacent change is the ADR-0055 status-quo update.

## Not abstracted

- **Per-runtime CLI flags / argv.** Each launcher's `SPRING_AGENT_ARGV`
  contents (the exact `claude --print --dangerously-skip-permissions
  --mcp-config <path>` shape, the `codex` argv, the `gemini` argv) belong
  on the runtime catalogue and the per-launcher source docstrings, not
  here. The contract is "the launcher stamps `SPRING_AGENT_ARGV` and the
  bridge exec's it"; the *contents* of that argv are out of scope.
- **Bundle wire format.** [ADR-0055 §3](0055-pull-based-agent-bootstrap.md)
  defines the `GET /v1/bootstrap/agents/{agentId}` JSON shape. This ADR
  references it; it does not redo it.
- **MCP server topology.** [ADR-0057](0057-sidecar-local-mcp-server.md)
  decides the stdio sidecar-local MCP topology. This ADR references it as
  the source of the per-turn sidecar-MCP-server-mode child in §3 and the
  outbound worker MCP call in §4.2; it does not re-decide the topology.
- **Agent-side YAML schema.** The agent definition YAML, the `(runtime,
  model)` schema, and the manifest reshape are
  [ADR-0038](0038-agent-runtime-and-model-provider-split.md)'s. This ADR
  catalogues the env vars derived from those decisions, not the schemas.
- **Bridge versioning policy.** [ADR-0027](0027-agent-image-conformance-contract.md)'s
  N-2 backward compatibility on the bridge stands. This ADR catalogues the
  `x-spring-voyage-bridge-version` header surface; it does not re-decide
  the compatibility window.

## Revisit criteria

- A second inbound port on the agent container (e.g. a debug / live-log
  socket the platform calls into). The current contract is "one inbound
  endpoint" and §4.1 makes that explicit; a second port is the trigger to
  amend §4.1 with a justification.
- A non-`SPRING_*`, non-upstream env-var prefix the platform wants to
  stamp (e.g. a `SV_*` re-prefix to align with the OSS-naming convention
  the operator surface uses). The current decision is `SPRING_*`; a
  prefix change is an ADR amendment, not a launcher-by-launcher edit.
- A container-supervised process that is not the long-running sidecar
  or a per-turn CLI child (e.g. a long-running connector daemon). §3's
  table marks `supervisord` as "not present"; a daemon-shape addition
  is the trigger to revisit.
- A future MCP-spec revision that mandates discovery or token-issuance
  metadata on stdio servers; named here for symmetry with
  [ADR-0057](0057-sidecar-local-mcp-server.md)'s revisit criteria.
- Horizontal scale-out of the worker (more than one `spring-worker`)
  interacts with the per-turn MCP token store
  ([ADR-0054 §5](0054-one-mcp-server-one-execution-host.md)) the same way
  it does today; this record does not change that constraint but inherits
  its revisit trigger.
