// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

// Gemini-specific bridge wiring (ADR-0041 / #2103). Self-contained:
// the test file constructs its own A2AHandler with the Gemini flag
// values the .NET launcher (`GeminiLauncher.cs`) emits in
// SPRING_THREAD_ID_ARG_CREATE / _RESUME, and asserts cold-start /
// resume / restart-survival end-to-end at the bridge layer. The
// general thread-binding mechanics live in `a2a.test.ts` —
// duplicating them per-runtime would create a maintenance trap and
// collide with the parallel #2102 (Codex) wiring landing in this same
// directory; this file pins ONLY the Gemini-specific flag values.

import { strict as assert } from "node:assert";
import * as fs from "node:fs";
import * as os from "node:os";
import * as path from "node:path";
import { describe, it } from "node:test";

import { A2AHandler } from "../src/a2a.ts";
import { ThreadIdRegistry } from "../src/threads.ts";

const PROCESS_NODE = process.execPath;

// Stub CLI: prints its own argv (post node + -e) as JSON so the test
// can read back what the bridge appended after [createArg|resumeArg, threadId].
const ARGV_DUMP_SCRIPT = "process.stdout.write(JSON.stringify(process.argv.slice(1)))";

// Verbatim copies of the const values in
// `src/Cvoya.Spring.AgentRuntimes/Launchers/GeminiLauncher.cs` —
// these tests fail loudly if either side drifts.
const GEMINI_CREATE_ARG = "--session-id";
const GEMINI_RESUME_ARG = "--resume";

function workspaceTempdir(): string {
  return fs.mkdtempSync(path.join(os.tmpdir(), "sv-bridge-gemini-"));
}

function makeHandler(workspacePath: string, registry: ThreadIdRegistry): A2AHandler {
  return new A2AHandler({
    agentName: "gemini-agent",
    // `--` so node forwards subsequent args (--session-id / --resume)
    // to the script's process.argv instead of trying to interpret them.
    agentArgv: [PROCESS_NODE, "-e", ARGV_DUMP_SCRIPT, "--"],
    port: 8999,
    cancelGraceMs: 200,
    spawnEnv: { ...process.env, SPRING_WORKSPACE_PATH: workspacePath },
    threadBinding: { createArg: GEMINI_CREATE_ARG, resumeArg: GEMINI_RESUME_ARG },
    threadIdRegistry: registry,
  });
}

describe("Gemini A2AHandler thread-id wiring (ADR-0041 / #2103)", () => {
  it("first message on a thread spawns gemini with --session-id <thread.id>", async () => {
    const ws = workspaceTempdir();
    try {
      const handler = makeHandler(ws, new ThreadIdRegistry(ws));
      const threadId = "11111111-2222-3333-4444-555555555555";

      const res = await handler.handle({
        jsonrpc: "2.0",
        method: "message/send",
        params: {
          message: {
            contextId: threadId,
            parts: [{ text: "hello gemini" }],
          },
        },
        id: 1,
      });

      const task = res.result as Record<string, unknown>;
      assert.equal((task["status"] as Record<string, unknown>)["state"], "completed");
      const artifacts = task["artifacts"] as Array<{ parts: Array<{ text: string }> }>;
      const childArgv = JSON.parse(artifacts[0]?.parts[0]?.text ?? "[]") as string[];
      assert.deepEqual(childArgv.slice(-2), [GEMINI_CREATE_ARG, threadId]);
    } finally {
      fs.rmSync(ws, { recursive: true, force: true });
    }
  });

  it("second message on the same thread spawns gemini with --resume <thread.id>", async () => {
    const ws = workspaceTempdir();
    try {
      const registry = new ThreadIdRegistry(ws);
      const handler = makeHandler(ws, registry);
      const threadId = "22222222-3333-4444-5555-666666666666";

      // First send establishes the thread (covered by the previous test).
      await handler.handle({
        jsonrpc: "2.0",
        method: "message/send",
        params: { message: { contextId: threadId, parts: [{ text: "first" }] } },
        id: 1,
      });

      // Second send must use --resume.
      const res = await handler.handle({
        jsonrpc: "2.0",
        method: "message/send",
        params: { message: { contextId: threadId, parts: [{ text: "second" }] } },
        id: 2,
      });

      const task = res.result as Record<string, unknown>;
      const artifacts = task["artifacts"] as Array<{ parts: Array<{ text: string }> }>;
      const childArgv = JSON.parse(artifacts[0]?.parts[0]?.text ?? "[]") as string[];
      assert.deepEqual(childArgv.slice(-2), [GEMINI_RESUME_ARG, threadId]);
    } finally {
      fs.rmSync(ws, { recursive: true, force: true });
    }
  });

  it("after container restart (fresh registry, same workspace) next send uses --resume", async () => {
    // ADR-0041 acceptance: session files live on the per-agent workspace
    // volume (Gemini under $GEMINI_CLI_HOME/.gemini/tmp/...) and the
    // bridge's create-vs-resume marker also persists under the same
    // workspace. Simulate restart by constructing a fresh handler+registry
    // with the same workspace path — the marker file from the first run
    // is rehydrated and the next message goes straight to --resume.
    const ws = workspaceTempdir();
    try {
      const threadId = "33333333-4444-5555-6666-777777777777";

      // First "container generation" — sends one message, persists the id.
      {
        const handler = makeHandler(ws, new ThreadIdRegistry(ws));
        await handler.handle({
          jsonrpc: "2.0",
          method: "message/send",
          params: { message: { contextId: threadId, parts: [{ text: "pre-restart" }] } },
          id: 1,
        });
      }

      // Second "container generation" — fresh handler, fresh registry.
      const handler = makeHandler(ws, new ThreadIdRegistry(ws));
      const res = await handler.handle({
        jsonrpc: "2.0",
        method: "message/send",
        params: { message: { contextId: threadId, parts: [{ text: "post-restart" }] } },
        id: 2,
      });

      const task = res.result as Record<string, unknown>;
      const artifacts = task["artifacts"] as Array<{ parts: Array<{ text: string }> }>;
      const childArgv = JSON.parse(artifacts[0]?.parts[0]?.text ?? "[]") as string[];
      assert.deepEqual(childArgv.slice(-2), [GEMINI_RESUME_ARG, threadId]);
    } finally {
      fs.rmSync(ws, { recursive: true, force: true });
    }
  });
});
