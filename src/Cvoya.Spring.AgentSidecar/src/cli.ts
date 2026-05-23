#!/usr/bin/env node
// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

// Process entrypoint. tini (in the agent-base image) is PID 1; this file
// is the long-running Node process tini supervises in the default (A2A
// bridge) mode. ADR-0057: the same binary, invoked with `mcp` as the
// first non-Node argv, runs a stdio MCP server that proxies to the
// worker — spawned per turn by the CLI's `.mcp.json`.
//
// Mode selection:
//
//   node cli.js                       → A2A bridge (default, long-running)
//   node cli.js mcp                   → MCP-server (stdio, per-turn child)
//
// Signal handling for the bridge mode is minimal: SIGTERM/SIGINT
// initiate a graceful HTTP close, then exit. The MCP-server mode exits
// on stdin "end" — the CLI closes the pipe when its tool-use round
// ends — so it does not register signal handlers itself.

import { createBootstrapFetcherFromEnv } from "./bootstrap.js";
import { loadConfigFromEnv } from "./config.js";
import { runMcpServerMode } from "./mcp-server.js";
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
  // argv[0] = node, argv[1] = path to this script, argv[2..] = our args.
  // ADR-0057: a single `mcp` token switches us into stdio-MCP-server
  // mode. Everything else falls through to the long-running A2A bridge,
  // which is the default entrypoint the agent-base image runs.
  const mode = process.argv[2];
  if (mode === "mcp") {
    try {
      await runMcpServerMode();
    } catch (err) {
      log("error", "MCP-server mode crashed", { error: (err as Error).message });
      process.exit(1);
    }
    // runMcpServerMode resolves when stdin ends; exit cleanly so the CLI
    // does not see a zombie child.
    return;
  }

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
