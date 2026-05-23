#!/usr/bin/env node
// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

// Process entrypoint. tini (in the agent-base image) is PID 1; this file
// is the long-running Node process tini supervises. Signal handling is
// minimal: SIGTERM/SIGINT initiate a graceful HTTP close, then exit.

import { createBootstrapFetcherFromEnv } from "./bootstrap.js";
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

async function main(): Promise<void> {
  let config;
  try {
    config = loadConfigFromEnv();
  } catch (err) {
    log("error", "failed to load bridge config", { error: (err as Error).message });
    process.exit(2);
  }

  // ADR-0055 §9: when the launcher stamps SPRING_BOOTSTRAP_URL /
  // SPRING_BOOTSTRAP_TOKEN (Wave 3), pull the bundle and materialise its
  // files onto the workspace volume BEFORE accepting HTTP traffic. The
  // listen() call below is gated on a successful first fetch — a failure
  // is fatal so an empty-workspace container cannot serve a turn.
  let bootstrapFetcher;
  try {
    bootstrapFetcher = createBootstrapFetcherFromEnv(process.env);
  } catch (err) {
    log("error", "bootstrap configuration invalid", { error: (err as Error).message });
    process.exit(2);
  }

  if (bootstrapFetcher !== null) {
    try {
      await bootstrapFetcher.fetchAndMaterialize();
      log("info", "bootstrap bundle materialised", {
        version: bootstrapFetcher.cachedVersion,
      });
    } catch (err) {
      log("error", "initial bootstrap fetch failed", { error: (err as Error).message });
      process.exit(2);
    }
  }

  const sidecar = createServer(config, process.env, bootstrapFetcher);

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

main().catch((err) => {
  log("error", "fatal", { error: (err as Error).message });
  process.exit(1);
});
