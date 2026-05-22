// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

// Per-turn MCP session-token rewrite (ADR-0052 §4, issue #2615).
//
// The launcher writes the agent's MCP config (`.mcp.json` for Claude Code
// and Codex; `.gemini/settings.json` for Gemini) once at container launch.
// Its `spring-voyage` server block's `Authorization` header is stamped with
// the launch-time MCP token — which, for a freshly-deployed persistent
// agent, is empty (ADR-0052 §3: the launch path no longer issues an MCP
// session).
//
// The worker-side dispatcher issues exactly one MCP session per turn and
// delivers its token in the A2A `message/send` metadata (`mcpToken`). This
// module closes the wiring gap: before each CLI spawn, it rewrites the
// `spring-voyage` server block's `Authorization` header in the config file
// at `SPRING_MCP_CONFIG` with that per-turn token, so every turn dials the
// worker-side McpServer with a token it actually issued for that turn.
//
// CLI-agnostic by design: a literal rewrite of the on-disk config works
// regardless of whether the wrapped CLI expands `${VAR}` references. The
// CLI re-reads its config on each process start, so the rewrite is picked
// up on the very next exec.

import * as fs from "node:fs";

// Env var the launcher sets to the absolute path of the MCP config file
// inside the container. When unset, the bridge skips the rewrite entirely
// — an agent launched without an MCP config has no `spring-voyage` block
// to rewrite.
export const MCP_CONFIG_ENV_VAR = "SPRING_MCP_CONFIG";

// Name of the MCP server block inside the config whose `Authorization`
// header carries the per-turn MCP session token. ADR-0051: a single
// platform MCP server (`spring-voyage`) serves every sv.* tool.
const MCP_SERVER_NAME = "spring-voyage";

export interface RewriteResult {
  // True iff the config file was found, contained a `spring-voyage` server
  // block, and its Authorization header was (re)written.
  rewritten: boolean;
  // Set when the rewrite was attempted but could not complete. The bridge
  // logs this but does not fail the turn — a stale (or empty) token still
  // lets the CLI start; it just loses the platform MCP tools.
  warning?: string;
}

/**
 * Rewrites the `spring-voyage` MCP server's `Authorization` header in the
 * config file at `configPath` to `Bearer <token>`.
 *
 * Best-effort and synchronous: the config file is a few KB and the write
 * must complete before the CLI is spawned. Any failure is returned as a
 * `warning` rather than thrown — a rewrite failure degrades to the
 * launch-time token instead of taking the whole turn down.
 *
 * Supports the shape every launcher emits:
 *   `mcpServers.<name>.headers.Authorization`
 * (Claude Code / Codex `.mcp.json`, Gemini `.gemini/settings.json`).
 *
 * No-ops (returns `rewritten: false`, no warning) when the config has no
 * `spring-voyage` server block — the agent was launched without the
 * platform MCP server.
 */
export function rewriteMcpToken(
  configPath: string,
  token: string,
): RewriteResult {
  let raw: string;
  try {
    raw = fs.readFileSync(configPath, "utf8");
  } catch (err) {
    return {
      rewritten: false,
      warning: `could not read MCP config at ${configPath}: ${(err as Error).message}`,
    };
  }

  // Strip a leading UTF-8 BOM. `JSON.parse` rejects a BOM (`Unexpected
  // token ﻿`), and a writer that emits one (e.g. .NET's static
  // `Encoding.UTF8`) would otherwise silently disable the rewrite.
  if (raw.charCodeAt(0) === 0xfeff) {
    raw = raw.slice(1);
  }

  let config: unknown;
  try {
    config = JSON.parse(raw);
  } catch (err) {
    return {
      rewritten: false,
      warning: `MCP config at ${configPath} is not valid JSON: ${(err as Error).message}`,
    };
  }

  if (!config || typeof config !== "object") {
    return { rewritten: false, warning: `MCP config at ${configPath} is not a JSON object` };
  }

  const servers = (config as Record<string, unknown>)["mcpServers"];
  if (!servers || typeof servers !== "object") {
    // No mcpServers map at all — nothing to rewrite.
    return { rewritten: false };
  }

  const springVoyage = (servers as Record<string, unknown>)[MCP_SERVER_NAME];
  if (!springVoyage || typeof springVoyage !== "object") {
    // Agent launched without the platform MCP server. Not an error.
    return { rewritten: false };
  }

  const serverObj = springVoyage as Record<string, unknown>;
  let headers = serverObj["headers"];
  if (!headers || typeof headers !== "object") {
    headers = {};
    serverObj["headers"] = headers;
  }
  (headers as Record<string, unknown>)["Authorization"] = `Bearer ${token}`;

  try {
    fs.writeFileSync(configPath, `${JSON.stringify(config, null, 2)}\n`, "utf8");
  } catch (err) {
    return {
      rewritten: false,
      warning: `could not write rewritten MCP config to ${configPath}: ${(err as Error).message}`,
    };
  }

  return { rewritten: true };
}
