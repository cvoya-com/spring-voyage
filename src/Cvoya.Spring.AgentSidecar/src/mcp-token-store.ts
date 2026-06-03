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
//     file before spawning the CLI; the MCP-server-mode invocation reads
//     that file at process startup and holds the token in memory for its
//     lifetime (≤ the turn lifetime — the CLI, and with it the child,
//     exits at turn end).
//
// Per-turn isolation (#3000). A single shared file
// (`$SPRING_WORKSPACE_PATH/.spring/bridge/mcp-token`) is only safe when
// turns run one-at-a-time. Under `SPRING_CONCURRENT_THREADS=true` the A2A
// handler does NOT serialise: concurrent turns overwrite that one file in
// the window between the write and the child's startup read, so a child
// can read a sibling turn's token — which the worker revokes at that
// sibling's turn-end, yielding a 401 (and, before that, wrong-thread
// attribution, since the token carries the turn's thread/message id). The
// fix gives each turn its own file, keyed by thread id
// (`resolvePerThreadMcpTokenPath`), and points the child at it via
// `MCP_TOKEN_PATH_ENV_VAR` on a per-turn clone of the CLI spawn env.
// Concurrent turns are always distinct threads, and the actor serialises
// turns within a thread, so the per-thread file never races. The shared
// path (`resolveMcpTokenPath`) is retained as a fallback for runtimes
// that do not propagate the CLI env to the child, and for turns with no
// thread id.
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
 * Resolves the absolute path of the shared per-agent MCP token file under
 * a workspace mount. Returns `null` when no workspace path was supplied —
 * appropriate for unit tests or for agents launched without the per-agent
 * volume (the latter is a misconfiguration in production but the helper
 * must not throw on absence).
 *
 * This is the legacy/shared path. Under concurrent threads it races (see
 * the file header, #3000); prefer {@link resolvePerThreadMcpTokenPath}.
 * It is retained as the fallback for runtimes that do not propagate the
 * CLI env to the MCP-server-mode child, and for turns with no thread id.
 */
export function resolveMcpTokenPath(workspacePath: string | undefined): string | null {
  if (!workspacePath || workspacePath.length === 0) {
    return null;
  }
  return path.join(workspacePath, BRIDGE_STATE_DIR, MCP_TOKEN_FILE);
}

/**
 * Env var name carrying the absolute path of THIS turn's MCP token file
 * to the MCP-server-mode child. The long-running sidecar sets it on a
 * per-turn clone of the CLI spawn env (#3000); the child prefers it over
 * the shared {@link resolveMcpTokenPath} so concurrent turns never read
 * each other's token. The value is a path, not the token itself, so it is
 * safe to pass through the process environment.
 */
export const MCP_TOKEN_PATH_ENV_VAR = "SPRING_MCP_TOKEN_PATH";

/**
 * Subdirectory (under {@link BRIDGE_STATE_DIR}) holding one token file per
 * conversation, keyed by the opaque platform-managed id. Kept inside the
 * bridge-owned namespace. The segment is neutral (`work/`, not `threads/`)
 * so no platform-managed path carries agent-facing "thread" vocabulary
 * (#3041); the id remains internal plumbing.
 */
const WORK_SUBDIR = "work";

/**
 * Resolves the per-conversation MCP token path,
 * `<workspace>/<BRIDGE_STATE_DIR>/work/<id>/<MCP_TOKEN_FILE>`.
 *
 * Returns `null` — so the caller falls back to the shared
 * {@link resolveMcpTokenPath} — when the workspace path or thread id is
 * absent, or when the thread id is not a safe single path segment
 * (defence against path traversal; platform thread ids are GUIDs and
 * always pass).
 *
 * #3000: a single shared token file races under
 * `SPRING_CONCURRENT_THREADS=true` — a concurrent turn overwrites it
 * between the write and the MCP-server-mode child's startup read, so a
 * child can read (and then be 401'd on) a sibling turn's token. One file
 * per thread removes the shared mutable cell; concurrent turns are always
 * distinct threads, and the actor serialises turns within a thread, so
 * the per-thread file itself never races.
 */
export function resolvePerThreadMcpTokenPath(
  workspacePath: string | undefined,
  threadId: string | undefined,
): string | null {
  if (!workspacePath || workspacePath.length === 0) {
    return null;
  }
  if (!threadId || !/^[A-Za-z0-9_-]+$/.test(threadId)) {
    return null;
  }
  return path.join(workspacePath, BRIDGE_STATE_DIR, WORK_SUBDIR, threadId, MCP_TOKEN_FILE);
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
