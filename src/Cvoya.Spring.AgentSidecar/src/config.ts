// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

// Bridge configuration sourced from environment variables. The dispatcher
// owns these values: it builds AgentLaunchSpec.EnvironmentVariables and
// AgentLaunchSpec.Argv, and the container runtime forwards them to the
// container as env vars. The bridge has no hard-coded knowledge of any
// specific CLI tool.

/**
 * How the bridge delivers the platform thread id to the spawned CLI on
 * each `message/send`. Per ADR-0041, the runtime's session identifier is
 * the platform's `thread.id` verbatim — no hashing, no derivation.
 *
 * CLI runtimes (Claude Code, Codex, Gemini) typically distinguish two
 * operations:
 *
 *   * **create** — first message on a thread; the CLI mints a fresh
 *     session keyed by the supplied id (e.g. `claude --session-id <id>`).
 *   * **resume** — subsequent messages; the CLI loads the existing
 *     session file from disk (e.g. `claude --resume <id>`).
 *
 * The bridge picks `create` vs `resume` based on whether it has already
 * seen the same thread id (in-memory + on-disk session-file probe). The
 * launcher only has to declare the two flag names.
 */
export interface ThreadBindingConfig {
  // Flag name for the first message on a thread, e.g. `--session-id`.
  // The bridge appends `[flag, threadId]` to argv.
  createArg: string;
  // Flag name for subsequent messages on the same thread, e.g. `--resume`.
  // The bridge appends `[flag, threadId]` to argv.
  resumeArg: string;
}

export interface BridgeConfig {
  // TCP port the bridge listens on. The dispatcher dials this port; the
  // default matches AgentLaunchSpec.A2APort (8999).
  port: number;

  // Argv vector the bridge spawns on each `message/send`. Encoded as a
  // JSON array string so we can preserve quoting/whitespace exactly. We
  // intentionally do *not* shell-split a SPRING_AGENT_CMD string; #1063
  // showed how that bites.
  agentArgv: string[];

  // Display name that surfaces on the Agent Card.
  agentName: string;

  // How long to wait after SIGTERM before SIGKILL during cancellation.
  cancelGraceMs: number;

  // Optional thread-id → CLI session-id binding. When set, the bridge
  // reads `params.message.contextId` (the A2A 0.3 thread id) and appends
  // the appropriate flag + id to argv on every `message/send`. Absent on
  // runtimes that have no session concept (or that take the thread id via
  // env var rather than argv — see ADR-0041 § "thread.id IS the session
  // identifier"). When absent, the bridge spawns argv unchanged.
  threadBinding?: ThreadBindingConfig;
}

function parseArgv(raw: string | undefined): string[] {
  if (!raw || raw.length === 0) {
    return [];
  }
  let parsed: unknown;
  try {
    parsed = JSON.parse(raw);
  } catch (err) {
    throw new Error(
      `SPRING_AGENT_ARGV must be a JSON array of strings; got: ${raw}. ` +
        `Underlying parse error: ${(err as Error).message}`,
    );
  }
  if (!Array.isArray(parsed) || !parsed.every((p) => typeof p === "string")) {
    throw new Error(
      `SPRING_AGENT_ARGV must be a JSON array of strings; got: ${raw}`,
    );
  }
  return parsed as string[];
}

function parsePort(raw: string | undefined, fallback: number): number {
  if (!raw) {
    return fallback;
  }
  const n = Number.parseInt(raw, 10);
  if (!Number.isFinite(n) || n <= 0 || n > 65535) {
    throw new Error(`AGENT_PORT must be a TCP port (1..65535); got: ${raw}`);
  }
  return n;
}

function parsePositiveInt(
  raw: string | undefined,
  fallback: number,
  name: string,
): number {
  if (!raw) {
    return fallback;
  }
  const n = Number.parseInt(raw, 10);
  if (!Number.isFinite(n) || n < 0) {
    throw new Error(`${name} must be a non-negative integer; got: ${raw}`);
  }
  return n;
}

function parseThreadBinding(env: NodeJS.ProcessEnv): ThreadBindingConfig | undefined {
  const createArg = env.SPRING_THREAD_ID_ARG_CREATE;
  const resumeArg = env.SPRING_THREAD_ID_ARG_RESUME;
  if (!createArg && !resumeArg) {
    return undefined;
  }
  // Either both or neither — partial config is a launcher bug we want to
  // surface loudly, not silently degrade to "spawn with no session id".
  if (!createArg || !resumeArg) {
    throw new Error(
      "SPRING_THREAD_ID_ARG_CREATE and SPRING_THREAD_ID_ARG_RESUME must be set together " +
        "(or both absent). Got CREATE=" +
        JSON.stringify(createArg ?? null) +
        ", RESUME=" +
        JSON.stringify(resumeArg ?? null) +
        ". See ADR-0041 § 'thread.id IS the session identifier'.",
    );
  }
  return { createArg, resumeArg };
}

export function loadConfigFromEnv(env: NodeJS.ProcessEnv = process.env): BridgeConfig {
  return {
    port: parsePort(env.AGENT_PORT, 8999),
    agentArgv: parseArgv(env.SPRING_AGENT_ARGV),
    agentName: env.AGENT_NAME ?? "Spring Voyage CLI Agent",
    cancelGraceMs: parsePositiveInt(env.AGENT_CANCEL_GRACE_MS, 5000, "AGENT_CANCEL_GRACE_MS"),
    threadBinding: parseThreadBinding(env),
  };
}
