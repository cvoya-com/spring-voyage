// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

// Per-thread session-binding helpers (ADR-0041 / issue #2094).
//
// The bridge maps the platform's `thread.id` (an A2A 0.3 `contextId`) onto
// the wrapped CLI's session identifier verbatim. The first send for a
// given thread.id needs to invoke the CLI with the runtime's "create-this-
// session-id" flag (e.g. `claude --session-id <id>`); every subsequent
// send needs the "resume" flag (e.g. `claude --resume <id>`). The bridge
// has no Claude/Codex/Gemini-specific knowledge; the launcher tells it
// which flag to use via SPRING_THREAD_ID_ARG_CREATE / _RESUME.
//
// To survive container restart we persist the set of seen thread ids on
// the per-agent workspace volume (D1 spec § 2.2.1, ADR-0029). The marker
// file is a newline-separated list under
// `$SPRING_WORKSPACE_PATH/.spring/bridge/seen-threads`. The
// volume mount is owned by the platform — when an agent's container is
// recreated on the same agent the file is still there and the bridge
// resumes correctly on the first message after restart. When the volume
// is wiped (operator-initiated) the bridge falls back to the create
// path, which is the right behaviour because the CLI's own session
// files were wiped at the same time.

import * as fs from "node:fs";
import * as path from "node:path";

/**
 * Subdirectory under `$SPRING_WORKSPACE_PATH` that the bridge owns. Any
 * other component writing under here is a bridge bug.
 */
export const BRIDGE_STATE_DIR = ".spring/bridge";

/**
 * File name (under `BRIDGE_STATE_DIR`) holding the newline-separated set
 * of thread ids the bridge has dispatched at least once. Using a flat
 * file rather than per-id sentinels keeps the on-disk write to a single
 * append per new thread.
 */
export const SEEN_THREADS_FILE = "seen-threads";

/**
 * Tracks which `thread.id` values this bridge has ever dispatched, with
 * persistence to the per-agent workspace volume so the answer survives
 * container restart. Synchronous I/O — the marker file is tiny (one UUID
 * per thread the agent has ever seen) and the alternative is racy.
 */
export class ThreadIdRegistry {
  private readonly seen: Set<string>;
  private readonly markerPath: string | null;

  /**
   * @param workspacePath Value of `$SPRING_WORKSPACE_PATH` (the per-agent
   *   workspace mount). When `undefined` or empty, the registry runs in
   *   memory-only mode — appropriate for unit tests but means the
   *   create/resume signal is lost on container restart.
   */
  constructor(workspacePath: string | undefined) {
    this.seen = new Set<string>();
    if (!workspacePath || workspacePath.length === 0) {
      this.markerPath = null;
      return;
    }
    const dir = path.join(workspacePath, BRIDGE_STATE_DIR);
    this.markerPath = path.join(dir, SEEN_THREADS_FILE);
    try {
      fs.mkdirSync(dir, { recursive: true });
    } catch {
      // Best-effort: if the workspace is read-only the registry still
      // works in memory; the bridge will cold-start every thread on
      // restart, which the CLI handles by failing the create call. Not
      // ideal but the failure mode is loud, not silent corruption.
    }
    if (fs.existsSync(this.markerPath)) {
      try {
        const contents = fs.readFileSync(this.markerPath, "utf8");
        for (const line of contents.split("\n")) {
          const trimmed = line.trim();
          if (trimmed.length > 0) {
            this.seen.add(trimmed);
          }
        }
      } catch {
        // Same rationale: ignore corrupt marker file. The CLI itself
        // is the source of truth for its session files; the marker is
        // just an optimization to pick the right flag.
      }
    }
  }

  /**
   * Returns `true` iff the bridge has dispatched this thread id before.
   */
  has(threadId: string): boolean {
    return this.seen.has(threadId);
  }

  /**
   * Records a thread id as seen. Persists to the workspace marker file
   * if a workspace path was configured. Idempotent.
   */
  add(threadId: string): void {
    if (this.seen.has(threadId)) {
      return;
    }
    this.seen.add(threadId);
    if (this.markerPath === null) {
      return;
    }
    try {
      fs.appendFileSync(this.markerPath, `${threadId}\n`);
    } catch {
      // See constructor rationale.
    }
  }
}
