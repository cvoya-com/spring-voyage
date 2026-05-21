// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

// Per-turn orchestration callback-token refresh (issue #2580).
//
// The launcher writes `.mcp.json` once at container launch with the
// `spring-orchestration` MCP server's `Authorization` header set to a
// per-invocation callback JWT. That JWT has a 5-minute lifetime
// (`CallbackTokenOptions.Lifetime`). A persistent agent container lives
// for hours and re-execs the wrapped CLI on every `message/send`, but the
// static `.mcp.json` token is never refreshed — after 5 minutes it is
// expired, the dispatcher rejects it with a 401, and the CLI drops the
// `spring-orchestration` MCP server for the rest of the session, so
// `delegate_to` / `fanout_to` are gone.
//
// The dispatcher already mints a fresh per-message callback token
// (`A2AExecutionDispatcher.IssuePerMessageCallbackToken`) carrying the
// current turn's `threadId` / `messageId`, and delivers it in the A2A
// `message/send` metadata. The bridge already extracts it (see
// `a2a.ts`). This module closes the wiring gap: before each exec, it
// rewrites the `spring-orchestration` server's `Authorization` header in
// `.mcp.json` with that per-message token, so every turn dials the
// dispatcher with a fresh, correctly thread-scoped token.
//
// CLI-agnostic by design: a literal rewrite of the on-disk config works
// regardless of whether the wrapped CLI expands `${VAR}` references in
// `.mcp.json`. The CLI re-reads `.mcp.json` on each process start, so
// the rewrite is picked up on the very next exec.

import * as fs from "node:fs";

// Env var the launcher sets to the absolute path of the `.mcp.json`
// (or equivalent MCP config) file inside the container. When unset, the
// bridge skips the refresh entirely — an agent launched without
// orchestration tools has no `spring-orchestration` block to refresh.
export const ORCHESTRATION_MCP_CONFIG_ENV_VAR = "SPRING_ORCHESTRATION_MCP_CONFIG";

// Name of the MCP server block inside the config whose `Authorization`
// header carries the orchestration callback token. Matches
// `ClaudeCodeLauncher.SpringOrchestrationMcpServerName` /
// `CodexLauncher`'s `spring-orchestration` key.
const ORCHESTRATION_SERVER_NAME = "spring-orchestration";

export interface RefreshResult {
  // True iff the config file was found, contained a spring-orchestration
  // block, and its Authorization header was (re)written.
  refreshed: boolean;
  // Set when the refresh was attempted but could not complete. The
  // bridge logs this but does not fail the turn — a stale token still
  // lets the CLI start; it just loses delegate_to once expired.
  warning?: string;
}

/**
 * Rewrites the `spring-orchestration` MCP server's `Authorization`
 * header in the config file at `configPath` to `Bearer <token>`.
 *
 * Best-effort and synchronous: the config file is a few KB and the
 * write must complete before the CLI is spawned. Any failure is
 * returned as a `warning` rather than thrown — a refresh failure
 * degrades to the pre-#2580 behaviour (stale token) instead of taking
 * the whole turn down.
 *
 * Supports both shapes the launchers emit:
 *   - Claude Code / Codex: `mcpServers.<name>.headers.Authorization`
 *
 * No-ops (returns `refreshed: false`, no warning) when the config has
 * no `spring-orchestration` block — the agent was launched without
 * orchestration tools.
 */
export function refreshOrchestrationToken(
  configPath: string,
  token: string,
): RefreshResult {
  let raw: string;
  try {
    raw = fs.readFileSync(configPath, "utf8");
  } catch (err) {
    return {
      refreshed: false,
      warning: `could not read MCP config at ${configPath}: ${(err as Error).message}`,
    };
  }

  let config: unknown;
  try {
    config = JSON.parse(raw);
  } catch (err) {
    return {
      refreshed: false,
      warning: `MCP config at ${configPath} is not valid JSON: ${(err as Error).message}`,
    };
  }

  if (!config || typeof config !== "object") {
    return { refreshed: false, warning: `MCP config at ${configPath} is not a JSON object` };
  }

  const servers = (config as Record<string, unknown>)["mcpServers"];
  if (!servers || typeof servers !== "object") {
    // No mcpServers map at all — nothing to refresh.
    return { refreshed: false };
  }

  const orchestration = (servers as Record<string, unknown>)[ORCHESTRATION_SERVER_NAME];
  if (!orchestration || typeof orchestration !== "object") {
    // Agent launched without orchestration tools. Not an error.
    return { refreshed: false };
  }

  const orchestrationObj = orchestration as Record<string, unknown>;
  let headers = orchestrationObj["headers"];
  if (!headers || typeof headers !== "object") {
    headers = {};
    orchestrationObj["headers"] = headers;
  }
  (headers as Record<string, unknown>)["Authorization"] = `Bearer ${token}`;

  try {
    fs.writeFileSync(configPath, `${JSON.stringify(config, null, 2)}\n`, "utf8");
  } catch (err) {
    return {
      refreshed: false,
      warning: `could not write refreshed MCP config to ${configPath}: ${(err as Error).message}`,
    };
  }

  return { refreshed: true };
}
