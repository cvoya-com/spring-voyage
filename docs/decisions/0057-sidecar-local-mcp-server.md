# 0057 — Agent-runtime MCP surface is sidecar-local

- **Status:** Proposed — moves the MCP server the agent's CLI dials from `spring-worker` (cross-network HTTP) into the per-agent TypeScript sidecar (stdio by default; loopback HTTP for runtimes that do not support stdio), eliminating MCP OAuth-2.1 discovery requirements without changing the per-turn-token model. Amends [ADR-0054](0054-one-mcp-server-one-execution-host.md) §2.
- **Date:** 2026-05-23
- **Related ADRs:** [0054](0054-one-mcp-server-one-execution-host.md) — amends §2 ("one McpServer, in the worker, served as POST /mcp/") so the *cross-network* hop is a platform-API call from the sidecar rather than an MCP transport from the CLI. The single tool-implementation surface (the worker's `McpServer` and its handlers) is unchanged; what moves is the *client* of that surface. [0055](0055-pull-based-agent-bootstrap.md) — the sidecar already pulls per-turn bundles from the worker; this record extends that internal channel to carry `tools/list` and `tools/call` traffic too. [0027](0027-agent-image-conformance-contract.md) — the sidecar already brokers the A2A 0.3.x in-container surface; gaining a co-located MCP server keeps the per-agent trust boundary intact.
- **Related code:** `src/Cvoya.Spring.AgentSidecar/src/server.ts`, `src/Cvoya.Spring.AgentSidecar/src/mcp-config.ts` (removed under this record), `src/Cvoya.Spring.Dapr/Mcp/McpServer.cs`, `src/Cvoya.Spring.Dapr/Mcp/McpServerOptions.cs`.
- **Related issues:** [#2666](https://github.com/cvoya-com/spring-voyage/issues/2666) — the symptom this record addresses by changing topology rather than serving OAuth-2.1 discovery metadata.

## Context

[ADR-0054](0054-one-mcp-server-one-execution-host.md) §2 placed the platform MCP server in the worker, served as `POST /mcp/` on a dedicated Kestrel port, with each agent runtime CLI dialling it across the tenant network. §5 made the credential a single per-turn bearer token issued by that worker `McpServer` and stamped into the CLI's `.mcp.json` by the sidecar before each spawn (`mcp-config.ts`).

The cross-network hop carries a cost the re-baseline did not surface: every CLI's HTTP-MCP transport probes for RFC 9728 / RFC 8414 OAuth-2.1 discovery metadata, and treats absence as *"this resource needs OAuth, we have no token, mark `needs-auth`"*. The bearer header is wire-compliant but not transport-compliant — the discovery surface is what the client gates `mcp_servers[].status` on, regardless of whether the header would have worked. #2666 documents this for Claude Code; Codex's and Gemini's HTTP-MCP transports are on the same trajectory.

The mitigation #2666 proposes is to serve stub OAuth metadata from the worker (`.well-known/oauth-protected-resource`, `.well-known/oauth-authorization-server`, `WWW-Authenticate: Bearer resource_metadata=...` on 401) — enough to satisfy the discovery probe without implementing an authorization server. It works for Claude Code today, but it inherits two problems:

1. **Each new CLI's HTTP-MCP transport is a separate compliance surface.** Codex might insist on a token endpoint with specific grant types; Gemini might require dynamic client registration; a future MCP-spec revision can tighten the contract. The stub grows.
2. **The discovery surface advertises an authorization server that does not exist.** Stricter clients can call the bluff — probe the advertised AS's `/token` with a degenerate grant and expect `unsupported_grant_type`, not 404.

The forcing question: do the CLI runtimes need to talk MCP across the network, or just to the platform? The CLI's tools, prompts, and reasoning all happen inside the agent container. The sidecar is already in that container, already brokers the per-turn token, already pulls per-turn bundles from the worker (#2665), and is the agent-side endpoint of the A2A wire. The cross-network MCP transport is the one boundary the sidecar is *not* currently brokering.

## Decision

### 1. The MCP server the CLI dials is sidecar-local

Every agent-runtime CLI (Claude Code, Codex, Gemini) dials a sidecar-hosted MCP server on a local transport — **stdio by default**, with a loopback HTTP transport (`127.0.0.1:<port>`) available for runtimes that do not support stdio. Neither transport carries OAuth-2.1 discovery requirements in any current MCP client implementation; both eliminate the `needs-auth` failure mode #2666 documents.

`.mcp.json` (and equivalents) names `spring-voyage` as a `command`-typed (stdio) MCP server pointing at the sidecar binary. The sidecar's entry point grows an MCP-server mode invoked by that command.

### 2. The sidecar is a thin proxy onto the platform API

The sidecar's MCP server implements `initialize` directly (returns the server capabilities the worker advertises today) and forwards everything else — `tools/list`, `tools/call`, future MCP surfaces — to the worker as an authenticated HTTP call carrying the per-turn token in the `Authorization` header. `tools/list` is cached for the duration of a turn so the proxy round-trip is paid at most once per turn.

The sidecar does **not** reimplement any `sv.<area>.<verb>` semantics. The worker's `McpServer` and its tool handlers remain the single source of truth for tool behaviour; the sidecar adds only protocol-translation and credential-injection. A new platform tool ships by registering it on the worker as today — no sidecar change required.

### 3. The per-turn token model is unchanged in shape, narrower in placement

The worker still issues one per-turn MCP session token at dispatch start and revokes it at turn-end (ADR-0054 §5). The token still rides A2A `message/send` metadata to the agent container (ADR-0029). What changes:

- The CLI no longer sees the token — it talks to the sidecar over stdio (or loopback) with no auth surface.
- The sidecar holds the token in-memory for the turn and uses it on its server-to-server platform-API calls.
- `mcp-config.ts` (the `.mcp.json` Authorization-header rewrite) is removed: the CLI's MCP target is a local sidecar command, not a remote HTTP endpoint, so there is no `headers.Authorization` to rewrite.

The token's lifecycle, contents, and security properties are unchanged; it simply no longer crosses the CLI process boundary.

### 4. The worker's MCP-HTTP transport becomes a platform-internal API

The worker keeps `McpServer` and its `POST /mcp/` route, but it is now reached only by the sidecar, not by CLIs. The OAuth-discovery requirements of public MCP HTTP transports do not apply to a server with exactly one known client living in the same trust boundary; the `.well-known/oauth-*` endpoints #2666 proposes are not built.

The wire between sidecar and worker can remain plain HTTP+bearer (today's shape) or graduate to a more compact internal RPC; that choice is downstream of this record and does not change the agent-facing contract.

### 5. ADR-0054's "one MCP server" principle stands

A CLI dials exactly one `spring-voyage` server, which happens to be the sidecar. The fact that the worker still hosts a tool-implementation surface internally is a deployment detail, not a tool-surface multiplication — the CLI cannot reach it, the model cannot enumerate it. Tool naming (`sv.<area>.<verb>`), the delivery-tool inventory (`sv.messaging.*`), and the gating model (ADR-0054 §§3–4, §6) are unchanged.

## Consequences

- **No OAuth-discovery surface to maintain.** The class of bugs #2666 represents — each CLI's HTTP transport demanding a slightly different discovery shape — does not exist on stdio / loopback transports.
- **One trust boundary instead of two.** The per-turn token is platform↔sidecar only; the CLI never holds it. A CLI compromise no longer leaks a usable bearer token onto a remote endpoint.
- **`mcp-config.ts` and the `.mcp.json` Authorization-rewrite path are deleted.** The sidecar's per-turn work shrinks to "hold the token in memory and use it on outbound proxy calls".
- **One extra hop per `tools/call`** (CLI → sidecar → worker). Within-container stdio plus intra-tenant HTTP latency is dominated by the LLM call itself; not a measurable runtime cost.
- **The sidecar grows an MCP-server mode** (~1 file using `@modelcontextprotocol/sdk`'s server side, plus a proxy adapter). The sidecar bundle stays small enough to ship as a Node SEA binary.
- **The worker's `McpServer` no longer needs RFC 9728 / RFC 8414 metadata endpoints.** #2666's proposed fix is not built; the issue closes by topology change.

## Revisit criteria

- A platform tool that requires direct CLI↔worker streaming semantics (large file transfer, long-poll, server-sent events) that cannot be tunnelled through the sidecar's MCP transport without unacceptable buffering. None of the v0.1 `sv.*` tools fit this shape.
- A future MCP-spec revision that mandates discovery or token-issuance metadata on stdio servers as well as HTTP servers. Unlikely — stdio servers are explicitly out of scope for the current MCP authorization spec — but worth noting.
- Horizontal scale-out of the worker (more than one `spring-worker`) interacts with ADR-0054 §5's in-process session store the same way it does today; this record does not change that constraint.
