// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

// Bridge configuration sourced from environment variables. The dispatcher
// owns these values: it builds AgentLaunchSpec.EnvironmentVariables and
// AgentLaunchSpec.Argv, and the container runtime forwards them to the
// container as env vars. The bridge has no hard-coded knowledge of any
// specific CLI tool.

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

export function loadConfigFromEnv(env: NodeJS.ProcessEnv = process.env): BridgeConfig {
  return {
    port: parsePort(env.AGENT_PORT, 8999),
    agentArgv: parseArgv(env.SPRING_AGENT_ARGV),
    agentName: env.AGENT_NAME ?? "Spring Voyage CLI Agent",
    cancelGraceMs: parsePositiveInt(env.AGENT_CANCEL_GRACE_MS, 5000, "AGENT_CANCEL_GRACE_MS"),
  };
}
