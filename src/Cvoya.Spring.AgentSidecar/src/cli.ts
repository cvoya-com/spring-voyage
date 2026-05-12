#!/usr/bin/env node
// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

// Process entrypoint. tini (in the agent-base image) is PID 1; this file
// is the long-running Node process tini supervises. Signal handling is
// minimal: SIGTERM/SIGINT initiate a graceful HTTP close, then exit.

import { loadConfigFromEnv } from "./config.js";
import { createServer } from "./server.js";
import { A2A_PROTOCOL_VERSION, BRIDGE_VERSION } from "./version.js";

function log(level: "info" | "warn" | "error", message: string, fields?: Record<string, unknown>): void {
  const entry = {
    ts: new Date().toISOString(),
    level,
    component: "spring-voyage-agent-sidecar",
    bridgeVersion: BRIDGE_VERSION,
    a2aProtocol: A2A_PROTOCOL_VERSION,
    message,
    ...fields,
  };
  process.stderr.write(`${JSON.stringify(entry)}\n`);
}

function main(): void {
  let config;
  try {
    config = loadConfigFromEnv();
  } catch (err) {
    log("error", "failed to load bridge config", { error: (err as Error).message });
    process.exit(2);
  }

  const sidecar = createServer(config);

  sidecar.server.on("error", (err: NodeJS.ErrnoException) => {
    log("error", "http server error", { error: err.message, code: err.code });
    process.exit(1);
  });

  sidecar.server.listen(config.port, () => {
    log("info", "bridge listening", {
      port: config.port,
      agentArgv: config.agentArgv,
      agentName: config.agentName,
    });
  });

  const shutdown = (signal: NodeJS.Signals) => {
    log("info", "shutting down", { signal });
    sidecar
      .close()
      .then(() => process.exit(0))
      .catch((err) => {
        log("error", "shutdown failed", { error: (err as Error).message });
        process.exit(1);
      });
  };

  process.on("SIGTERM", shutdown);
  process.on("SIGINT", shutdown);
}

main();
