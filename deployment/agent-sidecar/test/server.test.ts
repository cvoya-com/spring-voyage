// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

import { strict as assert } from "node:assert";
import { describe, it } from "node:test";

import { createServer } from "../src/server.ts";
import { A2A_PROTOCOL_VERSION, BRIDGE_VERSION } from "../src/version.ts";

const PROCESS_NODE = process.execPath;

async function withServer<T>(
  argv: string[],
  fn: (baseUrl: string) => Promise<T>,
): Promise<T> {
  const sidecar = createServer({
    port: 0, // ephemeral
    agentArgv: argv,
    agentName: "test-agent",
    cancelGraceMs: 200,
  });
  await new Promise<void>((resolve) => sidecar.server.listen(0, "127.0.0.1", resolve));
  const addr = sidecar.server.address();
  if (!addr || typeof addr === "string") {
    throw new Error("could not bind ephemeral port");
  }
  const baseUrl = `http://127.0.0.1:${addr.port}`;
  try {
    return await fn(baseUrl);
  } finally {
    await sidecar.close();
  }
}

describe("createServer", () => {
  it("serves the Agent Card at /.well-known/agent.json", async () => {
    await withServer([PROCESS_NODE], async (baseUrl) => {
      const res = await fetch(`${baseUrl}/.well-known/agent.json`);
      assert.equal(res.status, 200);
      assert.equal(
        res.headers.get("x-spring-voyage-bridge-version"),
        BRIDGE_VERSION,
      );
      const card = (await res.json()) as Record<string, unknown>;
      assert.equal(card.protocolVersion, A2A_PROTOCOL_VERSION);
      assert.equal(card.name, "test-agent");
    });
  });

  it("answers /healthz with 200 + bridge version", async () => {
    await withServer([PROCESS_NODE], async (baseUrl) => {
      const res = await fetch(`${baseUrl}/healthz`);
      assert.equal(res.status, 200);
      const body = (await res.json()) as { status: string; bridgeVersion: string };
      assert.equal(body.status, "ok");
      assert.equal(body.bridgeVersion, BRIDGE_VERSION);
    });
  });

  it("rejects unsupported routes with -32601 and 404", async () => {
    await withServer([PROCESS_NODE], async (baseUrl) => {
      const res = await fetch(`${baseUrl}/nope`);
      assert.equal(res.status, 404);
      const body = (await res.json()) as Record<string, unknown>;
      const error = body.error as { code: number };
      assert.equal(error.code, -32601);
    });
  });

  it("end-to-end JSON-RPC roundtrip via POST /", async () => {
    await withServer(
      [
        PROCESS_NODE,
        "-e",
        "let b='';process.stdin.on('data',c=>b+=c);process.stdin.on('end',()=>process.stdout.write('ack:'+b))",
      ],
      async (baseUrl) => {
        const res = await fetch(`${baseUrl}/`, {
          method: "POST",
          headers: { "Content-Type": "application/json" },
          body: JSON.stringify({
            jsonrpc: "2.0",
            method: "message/send",
            params: { message: { parts: [{ text: "ping" }] } },
            id: 42,
          }),
        });
        assert.equal(res.status, 200);
        const body = (await res.json()) as Record<string, unknown>;
        assert.equal(body.id, 42);
        const result = body.result as Record<string, unknown>;
        const status = result.status as Record<string, unknown>;
        assert.equal(status.state, "completed");
        const artifacts = result.artifacts as Array<{ parts: Array<{ text: string }> }>;
        assert.equal(artifacts[0]?.parts[0]?.text, "ack:ping");
      },
    );
  });

  it("rejects invalid JSON bodies with -32700", async () => {
    await withServer([PROCESS_NODE], async (baseUrl) => {
      const res = await fetch(`${baseUrl}/`, {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: "not json",
      });
      assert.equal(res.status, 400);
      const body = (await res.json()) as Record<string, unknown>;
      const error = body.error as { code: number };
      assert.equal(error.code, -32700);
    });
  });
});
