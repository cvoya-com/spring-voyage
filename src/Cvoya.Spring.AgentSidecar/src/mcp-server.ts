// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

// Sidecar-local MCP server (ADR-0057 §§1–2).
//
// Spawned by the agent's runtime CLI as a stdio MCP server. Speaks the
// MCP JSON-RPC 2.0 wire shape on stdin/stdout (one line-delimited JSON
// message per request/response) and proxies `initialize`, `tools/list`,
// `tools/call`, and any other MCP requests to the worker's `POST /mcp/`
// route — which is the worker's existing per-session `McpServer`,
// unchanged.
//
// The proxy adds exactly two things on top of the wire:
//
//   1. Credential injection: every outbound HTTP call carries
//      `Authorization: Bearer <per-turn MCP session token>`. The token
//      is read from disk on process start (see mcp-token-store.ts) —
//      the long-running A2A sidecar wrote it there for this turn.
//
//   2. Per-process caching: `tools/list` is cached for the lifetime of
//      the spawned MCP-server process (= one turn). The cache pays back
//      every time the CLI's MCP client re-asks the question after the
//      first `initialize`.
//
// Intentionally NO sv.* semantics live here. The worker's McpServer and
// its handlers remain the single source of truth — a new platform tool
// ships by registering it on the worker, no sidecar change required
// (ADR-0057 §2).
//
// We implement the protocol directly rather than depend on
// `@modelcontextprotocol/sdk` because the surface we need is small
// (three methods we look at + opaque proxy for everything else), the
// sidecar bundle has to stay small enough to ship as a Node SEA binary,
// and the wire is plain JSON-RPC 2.0 over newline-delimited stdio.

import * as readline from "node:readline";

import { MCP_TOKEN_PATH_ENV_VAR, readMcpToken, resolveMcpTokenPath } from "./mcp-token-store.js";
import { BRIDGE_VERSION } from "./version.js";

// Env var the long-running sidecar (and the launcher) sets to the
// per-agent workspace mount. The MCP-server-mode invocation reads the
// per-turn token from a known path under here.
const WORKSPACE_PATH_ENV_VAR = "SPRING_WORKSPACE_PATH";

// Env var the launcher stamps with the worker-side MCP endpoint URL the
// proxy calls. Reuses the URL the launcher used to put in `.mcp.json` so
// the launcher has exactly one MCP-endpoint value to manage.
//
// When unset, MCP-server-mode falls back to a sensible default for the
// in-container topology (`http://host.docker.internal:5050/mcp/`) — the
// same default the worker's McpServerOptions advertises.
const MCP_ENDPOINT_ENV_VAR = "SPRING_MCP_PROXY_URL";

const DEFAULT_WORKER_ENDPOINT = "http://host.docker.internal:5050/mcp/";

export interface McpServerModeDeps {
  // Where to read the per-turn MCP session token from. Defaults to the
  // workspace-resident store derived from SPRING_WORKSPACE_PATH.
  tokenPath?: string | null;
  // Worker-side MCP endpoint URL. Defaults to the SPRING_MCP_PROXY_URL
  // env var or, failing that, the in-container default.
  workerEndpoint?: string;
  // Stdin/stdout for the JSON-RPC stream. Defaults to process.stdin /
  // process.stdout. Injectable so tests can drive the loop with
  // PassThrough streams.
  stdin?: NodeJS.ReadableStream;
  stdout?: NodeJS.WritableStream;
  // Environment for default resolution. Defaults to process.env.
  env?: NodeJS.ProcessEnv;
  // Fetch implementation for the worker call. Defaults to global fetch
  // (Node 22+ ships native fetch). Tests inject a stub.
  fetchImpl?: typeof fetch;
}

interface JsonRpcMessage {
  jsonrpc?: string;
  id?: string | number | null;
  method?: string;
  params?: unknown;
  result?: unknown;
  error?: { code: number; message: string; data?: unknown };
}

/**
 * Resolves the token file this MCP-server-mode child should read, from
 * its environment.
 *
 * #3000: prefer the per-turn path the long-running sidecar pinned on this
 * CLI spawn's env (`SPRING_MCP_TOKEN_PATH`) — it is isolated per
 * concurrent thread, so the child never reads a sibling turn's token.
 * Fall back to the legacy shared per-agent path (derived from
 * `SPRING_WORKSPACE_PATH`) for runtimes that do not propagate the CLI env
 * to this child, and for turns with no thread id.
 */
function resolveTokenPathFromEnv(env: NodeJS.ProcessEnv): string | null {
  const perTurnPath = env[MCP_TOKEN_PATH_ENV_VAR];
  if (perTurnPath && perTurnPath.length > 0) {
    return perTurnPath;
  }
  return resolveMcpTokenPath(env[WORKSPACE_PATH_ENV_VAR]);
}

/**
 * Runs the MCP-server-mode loop. Resolves when the input stream ends
 * (the CLI closed its stdin to us → turn is winding down). Errors are
 * surfaced as JSON-RPC errors on the wire so the CLI's MCP client can
 * decide what to do; the loop itself only throws on unrecoverable I/O
 * (which Node maps to a non-zero process exit anyway).
 */
export async function runMcpServerMode(deps: McpServerModeDeps = {}): Promise<void> {
  const env = deps.env ?? process.env;
  const stdin = deps.stdin ?? process.stdin;
  const stdout = deps.stdout ?? process.stdout;
  const fetchImpl = deps.fetchImpl ?? fetch;
  const tokenPath =
    deps.tokenPath === undefined ? resolveTokenPathFromEnv(env) : deps.tokenPath;
  const workerEndpoint =
    deps.workerEndpoint ?? env[MCP_ENDPOINT_ENV_VAR] ?? DEFAULT_WORKER_ENDPOINT;

  // Read the per-turn token once at start. The long-running sidecar
  // wrote it for this turn before spawning the CLI; we hold it in
  // memory for the lifetime of this MCP-server-mode process. The next
  // turn = a new CLI spawn = a new MCP-server-mode process that reads
  // the new token. The path is resolved per turn (see
  // resolveTokenPathFromEnv / mcp-token-store.ts for the contract).
  const sessionToken = readMcpToken(tokenPath);

  // Cache `tools/list` for the process lifetime so the CLI's repeated
  // tools/list probes are served locally after the first proxy
  // roundtrip. The cache is keyed on `tools/list` only — `tools/call`
  // and other methods always proxy.
  let toolsListResult: unknown | undefined;

  // Stream reader: MCP stdio framing is one JSON-RPC message per line
  // (newline-delimited JSON). Using readline gives us per-line dispatch
  // without buffering the whole stream.
  const rl = readline.createInterface({ input: stdin, crlfDelay: Infinity });

  const writeLine = (msg: unknown): void => {
    try {
      stdout.write(`${JSON.stringify(msg)}\n`);
    } catch (err) {
      // stdout closed underneath us — the CLI has exited or the pipe
      // broke. Best-effort log to stderr and let the next stdin "end"
      // event tear us down cleanly.
      logStderr("error", "failed to write MCP response", {
        error: (err as Error).message,
      });
    }
  };

  const writeError = (id: string | number | null | undefined, code: number, message: string): void => {
    writeLine({
      jsonrpc: "2.0",
      id: id ?? null,
      error: { code, message },
    });
  };

  for await (const line of rl) {
    const trimmed = line.trim();
    if (trimmed.length === 0) {
      continue;
    }

    let msg: JsonRpcMessage;
    try {
      msg = JSON.parse(trimmed) as JsonRpcMessage;
    } catch (err) {
      writeError(null, -32700, `Parse error: ${(err as Error).message}`);
      continue;
    }

    if (msg.jsonrpc !== "2.0" || typeof msg.method !== "string") {
      writeError(msg.id ?? null, -32600, "Invalid JSON-RPC 2.0 request");
      continue;
    }

    // JSON-RPC notifications carry no id and expect no response. The
    // MCP spec uses notifications for `notifications/initialized` and
    // the cancellation channel; we ignore them on the proxy because
    // (a) the worker is request/response only and (b) cancellation is
    // already handled at the A2A layer by the long-running sidecar.
    const isNotification = msg.id === undefined || msg.id === null;

    try {
      if (msg.method === "tools/list" && toolsListResult !== undefined) {
        writeLine({ jsonrpc: "2.0", id: msg.id, result: toolsListResult });
        continue;
      }

      const proxied = await proxyToWorker(
        fetchImpl,
        workerEndpoint,
        sessionToken,
        msg,
      );

      if (isNotification) {
        // Drop the response — JSON-RPC notifications have no reply.
        continue;
      }

      if (msg.method === "tools/list" && proxied.result !== undefined) {
        toolsListResult = proxied.result;
      }

      writeLine({
        jsonrpc: "2.0",
        id: msg.id,
        ...(proxied.error
          ? { error: proxied.error }
          : { result: proxied.result }),
      });
    } catch (err) {
      // Network / transport failures surface as JSON-RPC -32603 so the
      // CLI sees a tool error rather than a closed pipe. The model can
      // then retry — the next attempt may succeed if the failure was
      // transient.
      writeError(
        msg.id ?? null,
        -32603,
        `MCP proxy to worker failed: ${(err as Error).message}`,
      );
    }
  }
}

/**
 * Single proxy call: POSTs the MCP JSON-RPC message to the worker with
 * the per-turn session token in the Authorization header, parses the
 * worker's response body, and returns either `{ result }` or
 * `{ error }` for the caller to forward on stdout.
 *
 * Notification calls (no `id`) still post the message — the worker may
 * have notification semantics in the future — but the caller drops the
 * response.
 */
async function proxyToWorker(
  fetchImpl: typeof fetch,
  endpoint: string,
  sessionToken: string,
  msg: JsonRpcMessage,
): Promise<{ result?: unknown; error?: { code: number; message: string; data?: unknown } }> {
  const res = await fetchImpl(endpoint, {
    method: "POST",
    headers: {
      "Content-Type": "application/json",
      // ADR-0057 §4: identify ourselves on the wire so the worker's
      // audit log can confirm requests come from a sidecar (and not
      // from a misconfigured CLI dialling the worker directly).
      "User-Agent": `spring-voyage-agent-sidecar/${BRIDGE_VERSION} proxy:mcp`,
      Authorization: `Bearer ${sessionToken}`,
    },
    body: JSON.stringify({
      jsonrpc: "2.0",
      id: msg.id ?? 0,
      method: msg.method,
      ...(msg.params !== undefined ? { params: msg.params } : {}),
    }),
  });

  if (!res.ok && res.status !== 200) {
    // Worker returned a non-200 HTTP status. The McpServer route uses
    // 401 for missing/invalid token and OK for everything else
    // (JSON-RPC errors ride 200 OK by JSON-RPC convention). A 401 here
    // means our per-turn token was rejected — surface it as a clearly-
    // labelled JSON-RPC -32001 so the CLI's MCP client sees something
    // actionable.
    const code = res.status === 401 ? -32001 : -32603;
    const body = await safeReadBody(res);
    return {
      error: {
        code,
        message: `Worker MCP endpoint returned HTTP ${res.status}: ${body}`,
      },
    };
  }

  let body: JsonRpcMessage;
  try {
    body = (await res.json()) as JsonRpcMessage;
  } catch (err) {
    return {
      error: {
        code: -32603,
        message: `Worker MCP endpoint returned non-JSON body: ${(err as Error).message}`,
      },
    };
  }

  if (body.error) {
    return { error: body.error };
  }
  return { result: body.result };
}

async function safeReadBody(res: Response): Promise<string> {
  try {
    return await res.text();
  } catch {
    return "<unreadable>";
  }
}

function logStderr(level: "info" | "warn" | "error", message: string, fields?: Record<string, unknown>): void {
  process.stderr.write(
    `${JSON.stringify({
      ts: new Date().toISOString(),
      level,
      component: "spring-voyage-agent-sidecar-mcp",
      bridgeVersion: BRIDGE_VERSION,
      message,
      ...fields,
    })}\n`,
  );
}
