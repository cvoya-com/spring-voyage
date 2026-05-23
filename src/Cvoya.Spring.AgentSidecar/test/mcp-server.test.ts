// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

import { strict as assert } from "node:assert";
import * as fs from "node:fs";
import * as os from "node:os";
import * as path from "node:path";
import { PassThrough } from "node:stream";
import { afterEach, beforeEach, describe, it } from "node:test";

import { runMcpServerMode } from "../src/mcp-server.ts";
import { MCP_TOKEN_FILE } from "../src/mcp-token-store.ts";
import { BRIDGE_STATE_DIR } from "../src/threads.ts";

let workspace: string;

beforeEach(() => {
  workspace = fs.mkdtempSync(path.join(os.tmpdir(), "sv-mcp-server-"));
});

afterEach(() => {
  fs.rmSync(workspace, { recursive: true, force: true });
});

/**
 * Drives the MCP-server-mode loop with the given input lines and a
 * stub fetch. Resolves to the array of newline-delimited JSON-RPC
 * responses the server wrote, along with the captured fetch calls so
 * the test can assert on the proxied request shape.
 */
async function drive(
  inputLines: string[],
  fetchStub: (url: string, init: RequestInit) => Promise<Response>,
  tokenPath: string | null,
  workerEndpoint = "http://worker.local/mcp/",
): Promise<{ responses: unknown[]; }> {
  const stdin = new PassThrough();
  const stdout = new PassThrough();
  for (const line of inputLines) {
    stdin.write(`${line}\n`);
  }
  stdin.end();

  const collected: Buffer[] = [];
  stdout.on("data", (chunk: Buffer) => collected.push(chunk));

  await runMcpServerMode({
    tokenPath,
    workerEndpoint,
    stdin,
    stdout,
    fetchImpl: fetchStub as typeof fetch,
  });

  const responses = Buffer.concat(collected)
    .toString("utf8")
    .split("\n")
    .filter((line) => line.length > 0)
    .map((line) => JSON.parse(line));
  return { responses };
}

function jsonResponse(body: unknown, init?: ResponseInit): Response {
  return new Response(JSON.stringify(body), {
    status: 200,
    headers: { "Content-Type": "application/json" },
    ...init,
  });
}

describe("runMcpServerMode", () => {
  it("proxies initialize to the worker with the per-turn bearer token", async () => {
    fs.mkdirSync(path.join(workspace, BRIDGE_STATE_DIR), { recursive: true });
    const tokenPath = path.join(workspace, BRIDGE_STATE_DIR, MCP_TOKEN_FILE);
    fs.writeFileSync(tokenPath, "per-turn-token-123");

    const captured: Array<{ url: string; init: RequestInit }> = [];
    const stub = async (url: string, init: RequestInit) => {
      captured.push({ url, init });
      return jsonResponse({
        jsonrpc: "2.0",
        id: 1,
        result: {
          protocolVersion: "2024-11-05",
          serverInfo: { name: "spring-voyage-mcp", version: "1.0.0" },
        },
      });
    };

    const { responses } = await drive(
      [JSON.stringify({ jsonrpc: "2.0", id: 1, method: "initialize" })],
      stub,
      tokenPath,
    );

    assert.equal(captured.length, 1);
    assert.equal(captured[0]?.url, "http://worker.local/mcp/");
    assert.equal(
      (captured[0]?.init?.headers as Record<string, string>).Authorization,
      "Bearer per-turn-token-123",
    );
    const proxied = JSON.parse(captured[0]?.init?.body as string);
    assert.equal(proxied.method, "initialize");

    assert.equal(responses.length, 1);
    const initRes = responses[0] as { id: number; result?: { serverInfo?: { name?: string } } };
    assert.equal(initRes.id, 1);
    assert.equal(initRes.result?.serverInfo?.name, "spring-voyage-mcp");
  });

  it("caches tools/list for the lifetime of the process", async () => {
    fs.mkdirSync(path.join(workspace, BRIDGE_STATE_DIR), { recursive: true });
    const tokenPath = path.join(workspace, BRIDGE_STATE_DIR, MCP_TOKEN_FILE);
    fs.writeFileSync(tokenPath, "tok");

    let calls = 0;
    const stub = async () => {
      calls += 1;
      return jsonResponse({
        jsonrpc: "2.0",
        id: 0,
        result: { tools: [{ name: "sv.messaging.send" }] },
      });
    };

    const { responses } = await drive(
      [
        JSON.stringify({ jsonrpc: "2.0", id: 1, method: "tools/list" }),
        JSON.stringify({ jsonrpc: "2.0", id: 2, method: "tools/list" }),
        JSON.stringify({ jsonrpc: "2.0", id: 3, method: "tools/list" }),
      ],
      stub,
      tokenPath,
    );

    // Three CLI tools/list calls → exactly one proxy round-trip to the worker.
    assert.equal(calls, 1);
    assert.equal(responses.length, 3);
    for (const res of responses) {
      const list = (res as { result?: { tools?: Array<{ name: string }> } }).result;
      assert.equal(list?.tools?.[0]?.name, "sv.messaging.send");
    }
  });

  it("forwards tools/call to the worker (no sidecar-side semantics)", async () => {
    fs.mkdirSync(path.join(workspace, BRIDGE_STATE_DIR), { recursive: true });
    const tokenPath = path.join(workspace, BRIDGE_STATE_DIR, MCP_TOKEN_FILE);
    fs.writeFileSync(tokenPath, "tok");

    const captured: Array<{ body: string }> = [];
    const stub = async (_url: string, init: RequestInit) => {
      captured.push({ body: init.body as string });
      return jsonResponse({
        jsonrpc: "2.0",
        id: 7,
        result: { content: [{ type: "text", text: "{}" }], isError: false },
      });
    };

    const { responses } = await drive(
      [
        JSON.stringify({
          jsonrpc: "2.0",
          id: 7,
          method: "tools/call",
          params: {
            name: "sv.messaging.send",
            arguments: { address: "agent:abc", message: "hi" },
          },
        }),
      ],
      stub,
      tokenPath,
    );

    assert.equal(captured.length, 1);
    const proxied = JSON.parse(captured[0]!.body);
    assert.equal(proxied.method, "tools/call");
    assert.equal(proxied.params.name, "sv.messaging.send");
    assert.equal(proxied.params.arguments.address, "agent:abc");

    const callRes = responses[0] as {
      result?: { content?: Array<{ text?: string }>; isError?: boolean };
    };
    assert.equal(callRes.result?.isError, false);
  });

  it("surfaces a worker 401 as JSON-RPC -32001 so the CLI can attribute it", async () => {
    const stub = async () =>
      new Response("unauthorized", { status: 401 });

    const { responses } = await drive(
      [JSON.stringify({ jsonrpc: "2.0", id: 1, method: "tools/list" })],
      stub,
      null, // no token store — proxy uses empty bearer, worker rejects
    );

    assert.equal(responses.length, 1);
    const errRes = responses[0] as { error?: { code: number; message: string } };
    assert.equal(errRes.error?.code, -32001);
    assert.match(errRes.error?.message ?? "", /401/);
  });

  it("returns -32603 when the worker is unreachable (network throws)", async () => {
    const stub = async () => {
      throw new Error("ECONNREFUSED");
    };

    const { responses } = await drive(
      [JSON.stringify({ jsonrpc: "2.0", id: 4, method: "tools/list" })],
      stub,
      null,
    );

    const errRes = responses[0] as { error?: { code: number; message: string } };
    assert.equal(errRes.error?.code, -32603);
    assert.match(errRes.error?.message ?? "", /ECONNREFUSED/);
  });

  it("rejects non-JSON lines with -32700 and continues processing", async () => {
    const stub = async () =>
      jsonResponse({ jsonrpc: "2.0", id: 1, result: { tools: [] } });

    const { responses } = await drive(
      [
        "{ not json",
        JSON.stringify({ jsonrpc: "2.0", id: 1, method: "tools/list" }),
      ],
      stub,
      null,
    );

    assert.equal(responses.length, 2);
    const parseErr = responses[0] as { error?: { code: number } };
    assert.equal(parseErr.error?.code, -32700);
    const okRes = responses[1] as { id: number };
    assert.equal(okRes.id, 1);
  });

  it("drops responses for JSON-RPC notifications (no id)", async () => {
    const stub = async () =>
      jsonResponse({ jsonrpc: "2.0", id: 0, result: null });

    const { responses } = await drive(
      [
        JSON.stringify({ jsonrpc: "2.0", method: "notifications/initialized" }),
      ],
      stub,
      null,
    );

    assert.equal(responses.length, 0);
  });

  it("falls back to empty token when the store has no file (next call hits worker 401)", async () => {
    let capturedAuth = "";
    const stub = async (_url: string, init: RequestInit) => {
      capturedAuth = (init.headers as Record<string, string>).Authorization;
      return jsonResponse({ jsonrpc: "2.0", id: 1, result: { tools: [] } });
    };

    await drive(
      [JSON.stringify({ jsonrpc: "2.0", id: 1, method: "tools/list" })],
      stub,
      resolveTokenPathOnEmptyWorkspace(workspace),
    );

    // No token file → empty bearer; the contract is "let the worker
    // reject", not "client-side gate".
    assert.equal(capturedAuth, "Bearer ");
  });
});

function resolveTokenPathOnEmptyWorkspace(ws: string): string {
  return path.join(ws, BRIDGE_STATE_DIR, MCP_TOKEN_FILE);
}
