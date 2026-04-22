// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

// Plain `node:http` server (no Express dependency) so the sidecar bundle
// is small enough to ship as a single Node SEA binary. Routes:
//
//   GET  /.well-known/agent.json   → Agent Card
//   GET  /healthz                  → readiness probe
//   POST /                         → A2A JSON-RPC 2.0 entry point
//
// All write paths are JSON-only and reject anything else with 4xx.

import http, { type IncomingMessage, type Server, type ServerResponse } from "node:http";

import { A2AHandler, type JsonRpcRequest } from "./a2a.js";
import type { BridgeConfig } from "./config.js";
import { BRIDGE_VERSION } from "./version.js";

const MAX_BODY_BYTES = 8 * 1024 * 1024;

export interface SidecarServer {
  server: Server;
  port: number;
  close: () => Promise<void>;
}

export function createServer(config: BridgeConfig, env: NodeJS.ProcessEnv = process.env): SidecarServer {
  const handler = new A2AHandler({
    agentName: config.agentName,
    agentArgv: config.agentArgv,
    port: config.port,
    cancelGraceMs: config.cancelGraceMs,
    spawnEnv: env,
  });

  const server = http.createServer((req, res) => {
    void route(handler, req, res).catch((err) => {
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

async function route(handler: A2AHandler, req: IncomingMessage, res: ServerResponse): Promise<void> {
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
