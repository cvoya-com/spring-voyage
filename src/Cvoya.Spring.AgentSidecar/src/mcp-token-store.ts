// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

// Per-turn MCP-session-token store, file-backed under the per-agent
// workspace volume (ADR-0057 §3).
//
// Topology:
//
//   * The long-running A2A sidecar receives every turn's MCP session token
//     in the A2A `message/send` metadata (`mcpToken`) — same delivery
//     channel ADR-0054 §5 specifies.
//   * The CLI is configured (via `.mcp.json` / `.gemini/settings.json`) to
//     spawn the sidecar binary in MCP-server mode as a stdio MCP server.
//     That spawned MCP-server process is a child of the CLI, not of the
//     long-running A2A sidecar, so the in-memory token the A2A sidecar
//     holds is not visible to it.
//   * To bridge the two, the A2A sidecar writes the per-turn token to a
//     file at `$SPRING_WORKSPACE_PATH/.spring-voyage-bridge/mcp-token`
//     on receipt of each `message/send`. The MCP-server-mode invocation
//     reads that file at process startup and holds the token in memory
//     for its lifetime. Each CLI spawn = one turn = one MCP-server
//     process lifetime ≤ turn lifetime, so the token in memory is
//     always the current turn's.
//
// The write is atomic (write-temp then rename) so the MCP-server-mode
// read never observes a torn token. Reads are best-effort: a missing
// or unreadable file means "no per-turn session yet"; the MCP-server
// proxy uses the empty string and the worker returns 401, which the
// CLI surfaces as a tool error the model can retry.

import * as crypto from "node:crypto";
import * as fs from "node:fs";
import * as path from "node:path";

import { BRIDGE_STATE_DIR } from "./threads.js";

/**
 * File name (under `BRIDGE_STATE_DIR`) the long-running sidecar writes the
 * per-turn MCP session token to, and the MCP-server-mode spawn reads it
 * from. One file per agent, overwritten atomically per turn.
 */
export const MCP_TOKEN_FILE = "mcp-token";

/**
 * Resolves the absolute path of the per-turn MCP token file under a
 * workspace mount. Returns `null` when no workspace path was supplied —
 * appropriate for unit tests or for agents launched without the per-agent
 * volume (the latter is a misconfiguration in production but the helper
 * must not throw on absence).
 */
export function resolveMcpTokenPath(workspacePath: string | undefined): string | null {
  if (!workspacePath || workspacePath.length === 0) {
    return null;
  }
  return path.join(workspacePath, BRIDGE_STATE_DIR, MCP_TOKEN_FILE);
}

export interface WriteResult {
  // True iff the token was written to disk.
  written: boolean;
  // Set when the write was attempted but could not complete. The caller
  // logs this but does not fail the turn — the MCP-server-mode spawn
  // will surface the missing token as a 401 from the worker, which the
  // model can retry.
  warning?: string;
}

/**
 * Writes the per-turn MCP session token to the workspace-resident token
 * file atomically (write-temp then rename). The file's mode is restricted
 * to 0o600 so a non-platform reader inside the container cannot lift the
 * token off disk.
 *
 * Synchronous: the file is tiny and the write must complete before the
 * CLI is spawned, so the MCP-server-mode invocation reads the current
 * turn's token (not the previous turn's).
 *
 * No-ops (returns `written: false`, no warning) when `tokenPath` is null
 * — i.e. when the sidecar runs without a workspace mount.
 */
export function writeMcpToken(tokenPath: string | null, token: string): WriteResult {
  if (tokenPath === null) {
    return { written: false };
  }

  const dir = path.dirname(tokenPath);
  try {
    fs.mkdirSync(dir, { recursive: true });
  } catch (err) {
    return {
      written: false,
      warning: `could not create MCP-token directory ${dir}: ${(err as Error).message}`,
    };
  }

  // Temp file in the same directory so the rename is on the same
  // filesystem (atomic). A random suffix avoids collisions if two
  // sends ever overlapped (the A2A handler serialises per-handler, but
  // defence-in-depth is cheap).
  const tmpPath = `${tokenPath}.${crypto.randomBytes(8).toString("hex")}.tmp`;
  try {
    fs.writeFileSync(tmpPath, token, { encoding: "utf8", mode: 0o600 });
    fs.renameSync(tmpPath, tokenPath);
  } catch (err) {
    try {
      fs.unlinkSync(tmpPath);
    } catch {
      // best-effort
    }
    return {
      written: false,
      warning: `could not write MCP token to ${tokenPath}: ${(err as Error).message}`,
    };
  }

  return { written: true };
}

/**
 * Reads the per-turn MCP session token off disk, or returns an empty
 * string when the file is absent / unreadable / empty.
 *
 * Called once at MCP-server-mode startup. The token is held in memory
 * for the lifetime of the spawned process, which is bounded by the
 * turn lifetime (the CLI exits at turn end and the OS reaps its child
 * MCP-server process). A new per-turn token is written by the long-
 * running A2A sidecar before the next CLI spawn, so the next MCP-
 * server-mode startup picks it up.
 */
export function readMcpToken(tokenPath: string | null): string {
  if (tokenPath === null) {
    return "";
  }
  try {
    return fs.readFileSync(tokenPath, "utf8").trim();
  } catch {
    return "";
  }
}
