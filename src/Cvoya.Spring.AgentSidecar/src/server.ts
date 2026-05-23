// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

// Plain `node:http` server (no Express dependency) so the sidecar bundle
// is small enough to ship as a single Node SEA binary. Routes:
//
//   GET  /.well-known/agent.json   → Agent Card
//   GET  /healthz                  → readiness probe
//   GET  /a2a/tools                → tool introspection (#2336 / Sub C)
//   POST /                         → A2A JSON-RPC 2.0 entry point
//
// All write paths are JSON-only and reject anything else with 4xx.

import fs from "node:fs";
import http, { type IncomingMessage, type Server, type ServerResponse } from "node:http";

import { A2AHandler, type JsonRpcRequest } from "./a2a.js";
import type { BootstrapFetcher } from "./bootstrap.js";
import type { BridgeConfig } from "./config.js";
import { ThreadIdRegistry } from "./threads.js";
import { BRIDGE_VERSION } from "./version.js";

const MAX_BODY_BYTES = 8 * 1024 * 1024;

// #2336 / Sub C of #2332: env var pointing at a JSON file containing
// the agent's image-tier tool surface. The platform-side introspector
// reads `GET /a2a/tools` at deploy / image-rotation time and caches
// the result onto the agent's `image_tools` column.
//
// The sidecar wraps an opaque CLI runtime — it has no in-process
// registry — so the agent author bakes the tool array into the image
// at this path. When the env var is unset or the file is missing,
// `/a2a/tools` returns `[]` so deploys of agents-without-tools succeed
// without per-image configuration.
const TOOLS_MANIFEST_ENV_VAR = "SPRING_TOOLS_MANIFEST";

// Env var the launcher sets to point at the per-agent workspace volume
// (D1 spec § 2.2.1, ADR-0029). The bridge persists its thread-id marker
// file under here so create/resume picks survive container restart
// (ADR-0041 / #2094 acceptance).
const WORKSPACE_PATH_ENV_VAR = "SPRING_WORKSPACE_PATH";

export interface SidecarServer {
  server: Server;
  port: number;
  close: () => Promise<void>;
}

export function createServer(
  config: BridgeConfig,
  env: NodeJS.ProcessEnv = process.env,
  bootstrapFetcher: BootstrapFetcher | null = null,
): SidecarServer {
  const threadIdRegistry = new ThreadIdRegistry(env[WORKSPACE_PATH_ENV_VAR]);
  const handler = new A2AHandler({
    agentName: config.agentName,
    agentArgv: config.agentArgv,
    port: config.port,
    cancelGraceMs: config.cancelGraceMs,
    spawnEnv: env,
    threadBinding: config.threadBinding,
    threadIdRegistry,
    bootstrapFetcher,
  });

  const toolsManifestPath = env[TOOLS_MANIFEST_ENV_VAR];

  const server = http.createServer((req, res) => {
    void route(handler, req, res, toolsManifestPath).catch((err) => {
      writeJson(res, 500, {
        jsonrpc: "2.0",
        id: null,
        error: { code: -32603, message: (err as Error).message },
      });
    });
  });

  return {
    server,
    port: config.port,
    close: () =>
      new Promise<void>((resolve, reject) =>
        server.close((err) => (err ? reject(err) : resolve())),
      ),
  };
}

async function route(
  handler: A2AHandler,
  req: IncomingMessage,
  res: ServerResponse,
  toolsManifestPath: string | undefined,
): Promise<void> {
  res.setHeader("x-spring-voyage-bridge-version", BRIDGE_VERSION);

  const url = req.url ?? "/";
  const method = req.method ?? "GET";

  if (method === "GET" && (url === "/.well-known/agent.json" || url === "/.well-known/agent-card.json")) {
    writeJson(res, 200, handler.buildAgentCard());
    return;
  }

  if (method === "GET" && url === "/healthz") {
    writeJson(res, 200, { status: "ok", bridgeVersion: BRIDGE_VERSION });
    return;
  }

  if (method === "GET" && url === "/a2a/tools") {
    writeJson(res, 200, readToolsManifest(toolsManifestPath));
    return;
  }

  if (method === "POST" && (url === "/" || url === "")) {
    let body: JsonRpcRequest;
    try {
      body = (await readJson(req)) as JsonRpcRequest;
    } catch (err) {
      writeJson(res, 400, {
        jsonrpc: "2.0",
        id: null,
        error: { code: -32700, message: `Parse error: ${(err as Error).message}` },
      });
      return;
    }
    const response = await handler.handle(body);
    const status = response.error ? 200 : 200;
    writeJson(res, status, response);
    return;
  }

  writeJson(res, 404, {
    jsonrpc: "2.0",
    id: null,
    error: { code: -32601, message: `No route for ${method} ${url}` },
  });
}

function readJson(req: IncomingMessage): Promise<unknown> {
  return new Promise((resolve, reject) => {
    const chunks: Buffer[] = [];
    let total = 0;
    req.on("data", (chunk: Buffer) => {
      total += chunk.length;
      if (total > MAX_BODY_BYTES) {
        reject(new Error(`Request body exceeds ${MAX_BODY_BYTES} bytes`));
        req.destroy();
        return;
      }
      chunks.push(chunk);
    });
    req.on("end", () => {
      if (chunks.length === 0) {
        resolve({});
        return;
      }
      try {
        resolve(JSON.parse(Buffer.concat(chunks).toString("utf8")));
      } catch (err) {
        reject(err);
      }
    });
    req.on("error", reject);
  });
}

function writeJson(res: ServerResponse, status: number, body: unknown): void {
  res.statusCode = status;
  res.setHeader("Content-Type", "application/json; charset=utf-8");
  res.end(JSON.stringify(body));
}

// #2336 / Sub C: reads the JSON tools manifest from disk on every
// request. Reading on every call (vs. caching at startup) lets an
// operator hot-swap the file inside a running container without
// restarting the sidecar — useful in tests and during incremental
// image authoring. The file is small (a few KB) and `/a2a/tools` is
// dispatched at most once per deploy / image rotation, so the I/O is
// not load-bearing.
function readToolsManifest(manifestPath: string | undefined): unknown[] {
  if (!manifestPath || manifestPath.length === 0) {
    return [];
  }
  let raw: string;
  try {
    raw = fs.readFileSync(manifestPath, "utf8");
  } catch {
    // Missing file or unreadable — treat as "no image-tier tools".
    // The dispatcher's introspector treats any failure as "no tools"
    // anyway; matching the same fail-quiet semantics here keeps the
    // two sides aligned.
    return [];
  }
  if (raw.trim().length === 0) {
    return [];
  }
  try {
    const parsed = JSON.parse(raw);
    return Array.isArray(parsed) ? parsed : [];
  } catch {
    return [];
  }
}
