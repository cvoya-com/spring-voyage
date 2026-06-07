// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

import { strict as assert } from "node:assert";
import * as fs from "node:fs";
import * as os from "node:os";
import * as path from "node:path";
import { describe, it } from "node:test";

import { A2AHandler } from "../src/a2a.ts";
import type { ThreadBindingConfig } from "../src/config.ts";
import {
  MCP_TOKEN_FILE,
  MCP_TOKEN_PATH_ENV_VAR,
  resolvePerThreadMcpTokenPath,
} from "../src/mcp-token-store.ts";
import { BRIDGE_STATE_DIR, SEEN_THREADS_FILE, ThreadIdRegistry } from "../src/threads.ts";
import { A2A_PROTOCOL_VERSION, BRIDGE_VERSION } from "../src/version.ts";

const PROCESS_NODE = process.execPath;

function makeHandler(
  argv: string[],
  spawnEnv: NodeJS.ProcessEnv = process.env,
  threadBinding?: ThreadBindingConfig,
  threadIdRegistry?: ThreadIdRegistry,
) {
  return new A2AHandler({
    agentName: "test-agent",
    agentArgv: argv,
    port: 8999,
    cancelGraceMs: 200,
    spawnEnv,
    threadBinding,
    threadIdRegistry,
  });
}

describe("A2AHandler.buildAgentCard", () => {
  it("declares the pinned A2A protocol version and bridge version", () => {
    const card = makeHandler([PROCESS_NODE]).buildAgentCard();
    assert.equal(card.protocolVersion, A2A_PROTOCOL_VERSION);
    assert.equal(card.version, BRIDGE_VERSION);
    assert.equal(card["x-spring-voyage-bridge-version"], BRIDGE_VERSION);
    assert.equal(card.name, "test-agent");
    assert.equal(card.capabilities.streaming, false);
    assert.equal(card.capabilities.pushNotifications, false);
    assert.equal(card.skills[0]?.id, "execute");
    assert.equal(card.interfaces[0]?.protocol, "jsonrpc/http");
  });
});

describe("A2AHandler.handle", () => {
  it("rejects unknown methods with -32601", async () => {
    const res = await makeHandler([PROCESS_NODE]).handle({
      jsonrpc: "2.0",
      method: "no/such/method",
      id: 1,
    });
    assert.equal(res.error?.code, -32601);
    assert.equal(res.id, 1);
  });

  it("rejects malformed JSON-RPC envelopes with -32600", async () => {
    const res = await makeHandler([PROCESS_NODE]).handle({
      jsonrpc: "1.0" as "2.0",
      method: "message/send",
      id: 7,
    });
    assert.equal(res.error?.code, -32600);
  });

  it("returns -32603 when SPRING_AGENT_ARGV is empty", async () => {
    const res = await makeHandler([]).handle({
      jsonrpc: "2.0",
      method: "message/send",
      params: { message: { parts: [{ text: "hi" }] } },
      id: "abc",
    });
    assert.equal(res.error?.code, -32603);
    assert.match(res.error?.message ?? "", /SPRING_AGENT_ARGV/);
  });

  it("round-trips a successful message/send to a stub CLI (A2A v0.3 wire shape)", async () => {
    // Wire shape (issue #1198): `message/send` result is the flat AgentTask
    // with a top-level `kind: "task"` discriminator — the .NET SDK's
    // SendMessageAsync reads result as A2AResponse via
    // A2AEventConverterViaKindDiscriminator, not a task/message wrapper.
    // Enum values are kebab-case-lower ("completed") per
    // KebabCaseLowerJsonStringEnumConverter. Part objects carry
    // `kind: "text"` per PartConverterViaKindDiscriminator.
    const handler = makeHandler([
      PROCESS_NODE,
      "-e",
      "let b='';process.stdin.on('data',c=>b+=c);process.stdin.on('end',()=>process.stdout.write('echo:'+b))",
    ]);
    const res = await handler.handle({
      jsonrpc: "2.0",
      method: "message/send",
      params: { message: { parts: [{ text: "ping" }] } },
      id: "task-1",
    });
    assert.equal(res.id, "task-1");
    const task = res.result as Record<string, unknown>;
    assert.ok(task, "expected a result payload");
    // A2A v0.3: result is the flat AgentTask, NOT wrapped under a `task` key.
    assert.equal(task["kind"], "task", "result must carry kind: task discriminator");
    assert.equal(typeof task["id"], "string", "result must have an id");
    const status = task["status"] as Record<string, unknown>;
    // Kebab-case-lower enum value per KebabCaseLowerJsonStringEnumConverter.
    assert.equal(status["state"], "completed");
    // contextId is [JsonRequired] on A2A.V0_3.AgentTask; the bridge mirrors
    // the task id since it has no separate conversation handle.
    assert.equal(typeof task["contextId"], "string");
    const artifacts = task["artifacts"] as Array<{ artifactId: string; parts: Array<{ kind: string; text: string }> }>;
    assert.equal(artifacts.length, 1);
    // Part objects carry kind: "text" per PartConverterViaKindDiscriminator.
    assert.equal(artifacts[0]?.parts[0]?.kind, "text");
    assert.equal(artifacts[0]?.parts[0]?.text, "echo:ping");
    assert.equal(task["x-spring-voyage-bridge-version"], BRIDGE_VERSION);
  });

  it("parses a json-format CLI result: reply is .result, cost rides on task metadata (#3073)", async () => {
    // With outputFormat "json" the bridge parses the Claude Code result object:
    // the assistant prose (`.result`) becomes the reply artifact, and the
    // turn's cost/usage is attached to the A2A task `metadata` for the host.
    const resultObject = {
      type: "result",
      subtype: "success",
      result: "the parsed answer",
      total_cost_usd: 0.05,
      usage: { input_tokens: 1000, output_tokens: 500 },
      modelUsage: { "claude-opus-4-8": {} },
    };
    const handler = new A2AHandler({
      agentName: "test-agent",
      agentArgv: [
        PROCESS_NODE,
        "-e",
        `process.stdout.write(${JSON.stringify(JSON.stringify(resultObject))})`,
      ],
      port: 8999,
      cancelGraceMs: 200,
      spawnEnv: process.env,
      outputFormat: "json",
    });

    const res = await handler.handle({
      jsonrpc: "2.0",
      method: "message/send",
      params: { message: { parts: [{ text: "ping" }] } },
      id: "json-1",
    });

    const task = res.result as Record<string, unknown>;
    assert.equal(task["kind"], "task");
    const artifacts = task["artifacts"] as Array<{ parts: Array<{ text: string }> }>;
    // The reply is the parsed prose, NOT the raw JSON wall.
    assert.equal(artifacts[0]?.parts[0]?.text, "the parsed answer");
    const metadata = task["metadata"] as Record<string, unknown>;
    assert.ok(metadata, "expected cost metadata on the task");
    assert.equal(metadata["sv.cost.usd"], 0.05);
    assert.equal(metadata["sv.usage.input_tokens"], 1000);
    assert.equal(metadata["sv.usage.output_tokens"], 500);
    assert.equal(metadata["sv.model"], "claude-opus-4-8");
  });

  it("parses a stream-json CLI result: reply from the result event, cost on metadata, tool calls surfaced (#2226)", async () => {
    // With outputFormat "stream-json" the bridge parses the Claude/Gemini
    // NDJSON event stream: the terminal result's `.result` becomes the reply
    // artifact, tool_use blocks become a `tool-calls` artifact, and the turn's
    // cost/usage rides on the A2A task `metadata`. The stub emits a reduced
    // Claude stream-json transcript.
    const events = [
      { type: "system", subtype: "init", session_id: "s1" },
      {
        type: "assistant",
        message: {
          role: "assistant",
          content: [
            { type: "text", text: "checking" },
            { type: "tool_use", name: "Bash", input: { command: "ls" } },
          ],
        },
      },
      {
        type: "result",
        subtype: "success",
        is_error: false,
        result: "the parsed answer",
        total_cost_usd: 0.05,
        usage: { input_tokens: 1000, output_tokens: 500 },
        modelUsage: { "claude-opus-4-8": {} },
      },
    ];
    const ndjson = events.map((e) => JSON.stringify(e)).join("\n") + "\n";
    const handler = new A2AHandler({
      agentName: "test-agent",
      agentArgv: [
        PROCESS_NODE,
        "-e",
        `process.stdout.write(${JSON.stringify(ndjson)})`,
      ],
      port: 8999,
      cancelGraceMs: 200,
      spawnEnv: process.env,
      outputFormat: "stream-json",
    });

    const res = await handler.handle({
      jsonrpc: "2.0",
      method: "message/send",
      params: { message: { parts: [{ text: "ping" }] } },
      id: "stream-1",
    });

    const task = res.result as Record<string, unknown>;
    assert.equal((task["status"] as Record<string, unknown>)["state"], "completed");
    const artifacts = task["artifacts"] as Array<{ artifactId: string; parts: Array<{ text: string }> }>;
    // First artifact is the reply (the parsed prose, not the JSON wall).
    assert.equal(artifacts[0]?.parts[0]?.text, "the parsed answer");
    // Tool calls surface under a known artifactId.
    const toolArtifact = artifacts.find((a) => a.artifactId === "tool-calls");
    assert.ok(toolArtifact, "expected a tool-calls artifact");
    assert.equal(toolArtifact!.parts[0]?.text, "Bash");
    // Cost rides on metadata.
    const metadata = task["metadata"] as Record<string, unknown>;
    assert.ok(metadata, "expected cost metadata on the task");
    assert.equal(metadata["sv.cost.usd"], 0.05);
    assert.equal(metadata["sv.usage.input_tokens"], 1000);
    assert.equal(metadata["sv.usage.output_tokens"], 500);
  });

  it("stream-json: an errored terminal result fails the task even on a clean exit (#2226)", async () => {
    // A CLI can exit 0 while reporting an in-band error in its terminal
    // result event. The bridge must surface that as task failure, not a
    // successful empty reply.
    const events = [
      { type: "system", subtype: "init", session_id: "s1" },
      { type: "result", subtype: "error", is_error: true, error: "model fatal error" },
    ];
    const ndjson = events.map((e) => JSON.stringify(e)).join("\n") + "\n";
    const handler = new A2AHandler({
      agentName: "test-agent",
      agentArgv: [PROCESS_NODE, "-e", `process.stdout.write(${JSON.stringify(ndjson)})`],
      port: 8999,
      cancelGraceMs: 200,
      spawnEnv: process.env,
      outputFormat: "stream-json",
    });

    const res = await handler.handle({
      jsonrpc: "2.0",
      method: "message/send",
      params: { message: { parts: [{ text: "ping" }] } },
      id: "stream-err-1",
    });

    const task = res.result as Record<string, unknown>;
    const status = task["status"] as Record<string, unknown>;
    assert.equal(status["state"], "failed");
    const message = status["message"] as Record<string, unknown>;
    const parts = message["parts"] as Array<{ text: string }>;
    assert.equal(parts[0]?.text, "model fatal error");
  });

  // Bug regression: the dispatcher sets the container's working directory to
  // the per-member workspace mount (AgentWorkspaceContract); the bridge
  // honours the same path on every CLI spawn so CWD-relative config
  // discovery (e.g. Claude Code's `.mcp.json`) sees the workspace files.
  // Without this the CLI was launched in the image's WORKDIR and saw no
  // MCP tools — which surfaced as a silent dispatch.
  it("spawns the CLI with cwd taken from SPRING_WORKSPACE_PATH", async () => {
    const workspaceDir = fs.mkdtempSync(path.join(os.tmpdir(), "sv-a2a-cwd-"));
    try {
      const handler = makeHandler(
        [PROCESS_NODE, "-e", "process.stdout.write(process.cwd())"],
        {
          PATH: process.env.PATH,
          SPRING_WORKSPACE_PATH: workspaceDir,
        },
      );

      const res = await handler.handle({
        jsonrpc: "2.0",
        method: "message/send",
        params: { message: { parts: [{ text: "" }] } },
        id: "cwd-1",
      });

      const task = res.result as Record<string, unknown>;
      const artifacts = task["artifacts"] as Array<{ parts: Array<{ text: string }> }>;
      // macOS reports temp dirs under /private/var; fs.realpath aligns the
      // expected path with what the child process actually sees.
      const expected = fs.realpathSync(workspaceDir);
      assert.equal(artifacts[0]?.parts[0]?.text, expected);
    } finally {
      fs.rmSync(workspaceDir, { recursive: true, force: true });
    }
  });

  // ADR-0057 §3: the bridge writes the per-turn MCP session token to a
  // workspace-resident file before each exec. The CLI's `.mcp.json`
  // points at the sidecar binary in MCP-server mode; that per-turn
  // child reads the token at startup and proxies it to the worker on
  // every tool call.
  it("writes the per-turn MCP token to the workspace-resident store before each exec", async () => {
    const ws = fs.mkdtempSync(path.join(os.tmpdir(), "sv-a2a-mcp-token-"));
    try {
      const handler = makeHandler(
        [PROCESS_NODE, "-e", "process.stdout.write('ok')"],
        {
          PATH: process.env.PATH,
          SPRING_WORKSPACE_PATH: ws,
        },
      );

      await handler.handle({
        jsonrpc: "2.0",
        method: "message/send",
        params: {
          message: {
            metadata: { mcpToken: "fresh-per-turn-token" },
            parts: [{ text: "ping" }],
          },
        },
        id: "mcp-token-write",
      });

      const tokenPath = path.join(ws, BRIDGE_STATE_DIR, MCP_TOKEN_FILE);
      assert.ok(fs.existsSync(tokenPath), `expected token file at ${tokenPath}`);
      assert.equal(fs.readFileSync(tokenPath, "utf8"), "fresh-per-turn-token");
    } finally {
      fs.rmSync(ws, { recursive: true, force: true });
    }
  });

  it("leaves the token file untouched when a message carries no mcpToken", async () => {
    const ws = fs.mkdtempSync(path.join(os.tmpdir(), "sv-a2a-mcp-token-"));
    try {
      // Seed a prior-turn token so we can confirm "no overwrite without a
      // new token" — a missing `mcpToken` on the next turn must NOT clear
      // the stale token, because doing so would degrade gracefully to a
      // 401 on the next tool call rather than reusing the (possibly still
      // valid) previous turn's token.
      const tokenDir = path.join(ws, BRIDGE_STATE_DIR);
      fs.mkdirSync(tokenDir, { recursive: true });
      const tokenPath = path.join(tokenDir, MCP_TOKEN_FILE);
      fs.writeFileSync(tokenPath, "prior-turn-token");

      const handler = makeHandler(
        [PROCESS_NODE, "-e", "process.stdout.write('ok')"],
        { PATH: process.env.PATH, SPRING_WORKSPACE_PATH: ws },
      );

      await handler.handle({
        jsonrpc: "2.0",
        method: "message/send",
        params: { message: { parts: [{ text: "ping" }] } },
        id: "mcp-no-token",
      });

      assert.equal(fs.readFileSync(tokenPath, "utf8"), "prior-turn-token");
    } finally {
      fs.rmSync(ws, { recursive: true, force: true });
    }
  });

  it("reports failed state with stderr text on non-zero CLI exit (A2A v0.3 wire shape)", async () => {
    // Wire shape (issue #1198): flat AgentTask with kind: "task", state
    // "failed" (kebab-case-lower), and status.message carrying kind: "message",
    // role: "agent" (kebab-case-lower), and parts with kind: "text".
    const handler = makeHandler([
      PROCESS_NODE,
      "-e",
      "process.stderr.write('boom');process.exit(3)",
    ]);
    const res = await handler.handle({
      jsonrpc: "2.0",
      method: "message/send",
      params: { message: { parts: [{ text: "" }] } },
      id: 9,
    });
    const task = res.result as Record<string, unknown>;
    // A2A v0.3: result is the flat AgentTask with kind discriminator.
    assert.equal(task["kind"], "task");
    const status = task["status"] as Record<string, unknown>;
    assert.equal(status["state"], "failed");
    const message = status["message"] as { kind: string; role: string; messageId: string; parts: Array<{ kind: string; text: string }> };
    // kind: "message" required by AgentMessage's polymorphic serialization.
    assert.equal(message.kind, "message");
    // role: "agent" (kebab-case-lower) per KebabCaseLowerJsonStringEnumConverter.
    assert.equal(message.role, "agent");
    assert.equal(typeof message.messageId, "string");
    // Part carries kind: "text".
    assert.equal(message.parts[0]?.kind, "text");
    assert.match(message.parts[0]?.text ?? "", /boom/);
  });

  it("tasks/get returns the cached terminal state for a completed task (A2A v0.3 wire shape)", async () => {
    // Kick off a successful send first, capture the task id from the
    // response, then assert tasks/get returns the same state without
    // re-running the CLI. tasks/get result is an AgentTask with kind: "task"
    // discriminator — the .NET SDK's GetTaskAsync deserializes result as
    // AgentTask directly; AgentTask is [JsonRequired] for "kind" per V0_3 SDK.
    const handler = makeHandler([PROCESS_NODE, "-e", "process.stdout.write('done')"]);
    const sendRes = await handler.handle({
      jsonrpc: "2.0",
      method: "message/send",
      params: { message: { parts: [{ text: "" }] } },
      id: 1,
    });
    // A2A v0.3: message/send result is the flat AgentTask (not wrapped).
    const sendTask = sendRes.result as { kind: string; id: string; status: { state: string } };
    assert.equal(sendTask.kind, "task");
    assert.equal(sendTask.status.state, "completed");

    const getRes = await handler.handle({
      jsonrpc: "2.0",
      method: "tasks/get",
      params: { id: sendTask.id },
      id: 2,
    });
    const getResult = getRes.result as { kind: string; id: string; status: { state: string } };
    assert.equal(getResult.kind, "task");
    assert.equal(getResult.id, sendTask.id);
    assert.equal(getResult.status.state, "completed");
  });

  it("tasks/cancel after terminal completion returns the cached state without re-running (A2A v0.3 wire shape)", async () => {
    // tasks/cancel result is an AgentTask with kind: "task" discriminator.
    // The .NET SDK's CancelTaskAsync deserializes result as AgentTask directly.
    // Already-completed task must not have its state flipped to canceled.
    const handler = makeHandler([PROCESS_NODE, "-e", "process.stdout.write('ok')"]);
    const sendRes = await handler.handle({
      jsonrpc: "2.0",
      method: "message/send",
      params: { message: { parts: [{ text: "" }] } },
      id: 1,
    });
    // A2A v0.3: message/send result is the flat AgentTask (not wrapped).
    const sendTask = sendRes.result as { kind: string; id: string; status: { state: string } };
    assert.equal(sendTask.kind, "task");
    assert.equal(sendTask.status.state, "completed");

    const cancelRes = await handler.handle({
      jsonrpc: "2.0",
      method: "tasks/cancel",
      params: { id: sendTask.id },
      id: 2,
    });
    const cancelResult = cancelRes.result as { kind: string; status: { state: string } };
    assert.equal(cancelResult.kind, "task");
    // Already completed → cancel must not flip the state.
    assert.equal(cancelResult.status.state, "completed");
  });

  it("tasks/get for an unknown id returns -32001", async () => {
    const res = await makeHandler([PROCESS_NODE]).handle({
      jsonrpc: "2.0",
      method: "tasks/get",
      params: { id: "does-not-exist" },
      id: 1,
    });
    assert.equal(res.error?.code, -32001);
  });

  it("tasks/cancel for an unknown id returns -32001", async () => {
    const res = await makeHandler([PROCESS_NODE]).handle({
      jsonrpc: "2.0",
      method: "tasks/cancel",
      params: { id: "does-not-exist" },
      id: 1,
    });
    assert.equal(res.error?.code, -32001);
  });

  it("tasks/cancel without params.id returns -32602", async () => {
    const res = await makeHandler([PROCESS_NODE]).handle({
      jsonrpc: "2.0",
      method: "tasks/cancel",
      params: {},
      id: 1,
    });
    assert.equal(res.error?.code, -32602);
  });

  it("aborting the request signal mid-handle SIGTERMs the spawned CLI and returns a canceled task (#2718)", async () => {
    // #2718: the bridge half of the "give up" symmetry. When the
    // dispatcher closes its inbound HTTP connection (HttpClient timeout,
    // actor turn cancel, dispose-mid-flight) the server passes the
    // request's AbortSignal into handle(), which wires it to the
    // per-task AbortController. Aborting it must SIGTERM the spawned
    // CLI so it does not outlive the dispatcher's view of the turn.
    // Without this the CLI keeps running after the dispatcher revokes
    // the MCP session and every subsequent tool call returns 401.
    const handler = makeHandler([
      PROCESS_NODE,
      "-e",
      // Long-running CLI: keeps the event loop alive indefinitely.
      // Without an abort it would never return — so a "completed"
      // task here means the wiring is broken.
      "setInterval(() => {}, 60000)",
    ]);

    const ac = new AbortController();
    // Abort once the spawn has had a moment to start.
    setTimeout(() => ac.abort(), 100);

    const res = await handler.handle(
      {
        jsonrpc: "2.0",
        method: "message/send",
        params: { message: { parts: [{ text: "ping" }] } },
        id: "abort-1",
      },
      ac.signal,
    );

    const task = res.result as Record<string, unknown>;
    assert.equal(task["kind"], "task");
    const status = task["status"] as Record<string, unknown>;
    assert.equal(status["state"], "canceled");
  });

  it("an already-aborted signal makes handle return a canceled task without leaving the CLI behind (#2718)", async () => {
    // Defence-in-depth for the race where the request closes before
    // handle() reads it (dispatcher gave up between TCP write and the
    // bridge starting to read). The signal is already aborted when
    // handle() runs; the spawn must still tear down promptly.
    const handler = makeHandler([
      PROCESS_NODE,
      "-e",
      "setInterval(() => {}, 60000)",
    ]);

    const ac = new AbortController();
    ac.abort();

    const res = await handler.handle(
      {
        jsonrpc: "2.0",
        method: "message/send",
        params: { message: { parts: [{ text: "ping" }] } },
        id: "abort-2",
      },
      ac.signal,
    );

    const task = res.result as Record<string, unknown>;
    const status = task["status"] as Record<string, unknown>;
    assert.equal(status["state"], "canceled");
  });
});

describe("A2AHandler.handle — thread-id binding (ADR-0041 / #2094)", () => {
  // The stub CLI prints its full argv (skipping argv[0/1] which are the
  // node binary + the -e flag's eval string) to stdout as JSON. The bridge
  // wraps spawn with the additional [flag, threadId] tokens at the tail
  // of the argv vector when a ThreadBindingConfig is configured.
  const ARGV_DUMP_SCRIPT = "process.stdout.write(JSON.stringify(process.argv.slice(1)))";

  function workspaceTempdir(): string {
    return fs.mkdtempSync(path.join(os.tmpdir(), "sv-bridge-a2a-"));
  }

  it("first message on a thread spawns CLI with the create flag (--session-id)", async () => {
    const ws = workspaceTempdir();
    try {
      const registry = new ThreadIdRegistry(ws);
      const handler = makeHandler(
        // `--` so node forwards subsequent args (--session-id, --resume) to
        // the script's process.argv instead of trying to interpret them.
        [PROCESS_NODE, "-e", ARGV_DUMP_SCRIPT, "--"],
        process.env,
        { createArg: "--session-id", resumeArg: "--resume" },
        registry,
      );

      const threadId = "11111111-2222-3333-4444-555555555555";
      const res = await handler.handle({
        jsonrpc: "2.0",
        method: "message/send",
        params: {
          message: {
            contextId: threadId,
            parts: [{ text: "hello" }],
          },
        },
        id: "first",
      });

      const task = res.result as Record<string, unknown>;
      assert.equal((task["status"] as Record<string, unknown>)["state"], "completed");
      const artifacts = task["artifacts"] as Array<{ parts: Array<{ text: string }> }>;
      const childArgv = JSON.parse(artifacts[0]?.parts[0]?.text ?? "[]") as string[];
      // Bridge appended [createArg, threadId] to the end of argv. The
      // stub's own -e arg lands at index 0 in the slice; we look at the
      // last two tokens regardless of argv length.
      assert.equal(childArgv[childArgv.length - 2], "--session-id");
      assert.equal(childArgv[childArgv.length - 1], threadId);

      // Acceptance: the registry must mark this thread as seen *and*
      // persist it so the next bridge boot resumes correctly.
      assert.equal(registry.has(threadId), true);
      const markerPath = path.join(ws, BRIDGE_STATE_DIR, SEEN_THREADS_FILE);
      assert.ok(fs.existsSync(markerPath), `expected marker file at ${markerPath}`);
      const persisted = fs.readFileSync(markerPath, "utf8");
      assert.match(persisted, new RegExp(threadId));
    } finally {
      fs.rmSync(ws, { recursive: true, force: true });
    }
  });

  it("second message on the same thread spawns CLI with the resume flag (--resume)", async () => {
    const ws = workspaceTempdir();
    try {
      const registry = new ThreadIdRegistry(ws);
      const handler = makeHandler(
        // `--` so node forwards subsequent args (--session-id, --resume) to
        // the script's process.argv instead of trying to interpret them.
        [PROCESS_NODE, "-e", ARGV_DUMP_SCRIPT, "--"],
        process.env,
        { createArg: "--session-id", resumeArg: "--resume" },
        registry,
      );

      const threadId = "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee";
      // First send establishes the thread. We don't assert on its argv
      // here — the previous test covers that.
      await handler.handle({
        jsonrpc: "2.0",
        method: "message/send",
        params: { message: { contextId: threadId, parts: [{ text: "hi" }] } },
        id: 1,
      });
      assert.equal(registry.has(threadId), true, "thread should be marked seen after first send");

      // Second send must use --resume.
      const res = await handler.handle({
        jsonrpc: "2.0",
        method: "message/send",
        params: { message: { contextId: threadId, parts: [{ text: "again" }] } },
        id: 2,
      });

      const task = res.result as Record<string, unknown>;
      assert.equal((task["status"] as Record<string, unknown>)["state"], "completed");
      const artifacts = task["artifacts"] as Array<{ parts: Array<{ text: string }> }>;
      const childArgv = JSON.parse(artifacts[0]?.parts[0]?.text ?? "[]") as string[];
      assert.equal(childArgv[childArgv.length - 2], "--resume");
      assert.equal(childArgv[childArgv.length - 1], threadId);
    } finally {
      fs.rmSync(ws, { recursive: true, force: true });
    }
  });

  it("after container restart (registry rehydrated from disk) the next send uses --resume", async () => {
    // ADR-0041 acceptance: session files survive a container restart.
    // Simulate restart by constructing a *fresh* handler+registry with the
    // same workspace path — the marker file written by the first run must
    // be loaded by the new ThreadIdRegistry so the next message goes
    // straight to --resume.
    const ws = workspaceTempdir();
    try {
      const threadId = "12345678-1234-1234-1234-123456789012";

      // First "container generation" — sends one message, persists the id.
      {
        const registry = new ThreadIdRegistry(ws);
        const handler = makeHandler(
          // `--` so node forwards subsequent args (--session-id, --resume) to
        // the script's process.argv instead of trying to interpret them.
        [PROCESS_NODE, "-e", ARGV_DUMP_SCRIPT, "--"],
          process.env,
          { createArg: "--session-id", resumeArg: "--resume" },
          registry,
        );
        await handler.handle({
          jsonrpc: "2.0",
          method: "message/send",
          params: { message: { contextId: threadId, parts: [{ text: "first turn" }] } },
          id: 1,
        });
      }

      // Second "container generation" — fresh handler, fresh registry,
      // same workspace. The next send must use --resume.
      const registry = new ThreadIdRegistry(ws);
      assert.equal(
        registry.has(threadId),
        true,
        "rehydrated registry should remember the seen thread id",
      );
      const handler = makeHandler(
        // `--` so node forwards subsequent args (--session-id, --resume) to
        // the script's process.argv instead of trying to interpret them.
        [PROCESS_NODE, "-e", ARGV_DUMP_SCRIPT, "--"],
        process.env,
        { createArg: "--session-id", resumeArg: "--resume" },
        registry,
      );
      const res = await handler.handle({
        jsonrpc: "2.0",
        method: "message/send",
        params: { message: { contextId: threadId, parts: [{ text: "post-restart turn" }] } },
        id: 2,
      });

      const task = res.result as Record<string, unknown>;
      const artifacts = task["artifacts"] as Array<{ parts: Array<{ text: string }> }>;
      const childArgv = JSON.parse(artifacts[0]?.parts[0]?.text ?? "[]") as string[];
      assert.equal(childArgv[childArgv.length - 2], "--resume");
      assert.equal(childArgv[childArgv.length - 1], threadId);
    } finally {
      fs.rmSync(ws, { recursive: true, force: true });
    }
  });

  it("two distinct threads are tracked independently (no cross-thread collision)", async () => {
    // ADR-0041 acceptance: no collision between two threads on the same
    // agent (concurrent_threads: true exercise). Each thread gets its
    // own create-then-resume sequence.
    const ws = workspaceTempdir();
    try {
      const registry = new ThreadIdRegistry(ws);
      const handler = makeHandler(
        // `--` so node forwards subsequent args (--session-id, --resume) to
        // the script's process.argv instead of trying to interpret them.
        [PROCESS_NODE, "-e", ARGV_DUMP_SCRIPT, "--"],
        process.env,
        { createArg: "--session-id", resumeArg: "--resume" },
        registry,
      );
      const threadA = "aaaa1111-0000-0000-0000-000000000000";
      const threadB = "bbbb2222-0000-0000-0000-000000000000";

      // First send on A → create.
      let res = await handler.handle({
        jsonrpc: "2.0",
        method: "message/send",
        params: { message: { contextId: threadA, parts: [{ text: "" }] } },
        id: 1,
      });
      let task = res.result as Record<string, unknown>;
      let artifacts = task["artifacts"] as Array<{ parts: Array<{ text: string }> }>;
      let argv = JSON.parse(artifacts[0]?.parts[0]?.text ?? "[]") as string[];
      assert.deepEqual(argv.slice(-2), ["--session-id", threadA]);

      // First send on B → create (independent of A's state).
      res = await handler.handle({
        jsonrpc: "2.0",
        method: "message/send",
        params: { message: { contextId: threadB, parts: [{ text: "" }] } },
        id: 2,
      });
      task = res.result as Record<string, unknown>;
      artifacts = task["artifacts"] as Array<{ parts: Array<{ text: string }> }>;
      argv = JSON.parse(artifacts[0]?.parts[0]?.text ?? "[]") as string[];
      assert.deepEqual(argv.slice(-2), ["--session-id", threadB]);

      // Second send on A → resume A.
      res = await handler.handle({
        jsonrpc: "2.0",
        method: "message/send",
        params: { message: { contextId: threadA, parts: [{ text: "" }] } },
        id: 3,
      });
      task = res.result as Record<string, unknown>;
      artifacts = task["artifacts"] as Array<{ parts: Array<{ text: string }> }>;
      argv = JSON.parse(artifacts[0]?.parts[0]?.text ?? "[]") as string[];
      assert.deepEqual(argv.slice(-2), ["--resume", threadA]);
    } finally {
      fs.rmSync(ws, { recursive: true, force: true });
    }
  });

  it("spawns argv unchanged when no thread-binding is configured", async () => {
    // Backwards-compatible default: a launcher that doesn't set the
    // SPRING_THREAD_ID_ARG_* env vars (e.g. a Python SDK runtime that
    // takes the thread id via SPRING_THREAD_ID env var instead) gets the
    // pre-#2094 behaviour — no extra argv tokens.
    const handler = makeHandler(
      [PROCESS_NODE, "-e", ARGV_DUMP_SCRIPT, "user-arg"],
      process.env,
      undefined, // no threadBinding
    );
    const res = await handler.handle({
      jsonrpc: "2.0",
      method: "message/send",
      params: {
        message: {
          contextId: "12345678-aaaa-bbbb-cccc-dddddddddddd",
          parts: [{ text: "" }],
        },
      },
      id: 1,
    });
    const task = res.result as Record<string, unknown>;
    const artifacts = task["artifacts"] as Array<{ parts: Array<{ text: string }> }>;
    const argv = JSON.parse(artifacts[0]?.parts[0]?.text ?? "[]") as string[];
    assert.deepEqual(argv, ["user-arg"]);
  });

  it("recovers from 'Session ID already in use' by retrying with --resume", async () => {
    // Regression: if the seen-threads marker was lost but Claude's session
    // file survived (crash-before-marker-write, or concurrent duplicate
    // delivery), the bridge uses --session-id and Claude rejects with
    // "is already in use". The handler must detect that, mark the thread as
    // seen, and immediately retry with --resume — the caller sees "completed".
    const ws = workspaceTempdir();
    try {
      const registry = new ThreadIdRegistry(ws);
      // Stub: --session-id → exit 1 + "already in use"; --resume → exit 0.
      const script =
        "const a=process.argv;" +
        "if(a.includes('--session-id')){" +
        "process.stderr.write('Error: Session ID x is already in use.');process.exit(1);" +
        "}else{process.stdout.write('resumed-ok');}";
      const handler = makeHandler(
        [PROCESS_NODE, "-e", script, "--"],
        process.env,
        { createArg: "--session-id", resumeArg: "--resume" },
        registry,
      );
      const threadId = "cccccccc-dddd-eeee-ffff-000000000000";
      const res = await handler.handle({
        jsonrpc: "2.0",
        method: "message/send",
        params: { message: { contextId: threadId, parts: [{ text: "hi" }] } },
        id: "recover",
      });
      const task = res.result as Record<string, unknown>;
      assert.equal((task["status"] as Record<string, unknown>)["state"], "completed");
      const artifacts = task["artifacts"] as Array<{ parts: Array<{ text: string }> }>;
      assert.equal(artifacts[0]?.parts[0]?.text, "resumed-ok");
      // Registry must now know about this thread so future sends use --resume.
      assert.equal(registry.has(threadId), true, "thread must be marked seen after recovery");
    } finally {
      fs.rmSync(ws, { recursive: true, force: true });
    }
  });

  it("does not mark thread as seen when CLI fails (next send retries create)", async () => {
    // If the CLI exits non-zero, the session file may not have been
    // written; the next send must re-attempt the create path so a
    // recoverable failure (transient credential error, ...) doesn't
    // permanently break that thread.
    const ws = workspaceTempdir();
    try {
      const registry = new ThreadIdRegistry(ws);
      const handler = makeHandler(
        [PROCESS_NODE, "-e", "process.stderr.write('boom');process.exit(1)"],
        process.env,
        { createArg: "--session-id", resumeArg: "--resume" },
        registry,
      );
      const threadId = "ffffffff-1111-2222-3333-444444444444";
      const res = await handler.handle({
        jsonrpc: "2.0",
        method: "message/send",
        params: { message: { contextId: threadId, parts: [{ text: "" }] } },
        id: 1,
      });
      const task = res.result as Record<string, unknown>;
      assert.equal((task["status"] as Record<string, unknown>)["state"], "failed");
      assert.equal(registry.has(threadId), false, "failed spawn must not mark thread seen");
    } finally {
      fs.rmSync(ws, { recursive: true, force: true });
    }
  });
});

describe("A2AHandler.handle — per-turn MCP token isolation (#3000)", () => {
  // The stub CLI echoes the SPRING_MCP_TOKEN_PATH it was spawned with, so
  // the test can confirm each turn's MCP-server-mode child is pointed at
  // its own per-thread token file rather than a shared, racing one.
  const TOKEN_PATH_DUMP = "process.stdout.write(process.env.SPRING_MCP_TOKEN_PATH || 'UNSET')";

  it("writes the token to a per-thread path and points the child at it via SPRING_MCP_TOKEN_PATH", async () => {
    const ws = fs.mkdtempSync(path.join(os.tmpdir(), "sv-a2a-token-iso-"));
    try {
      const spawnEnv: NodeJS.ProcessEnv = { PATH: process.env.PATH, SPRING_WORKSPACE_PATH: ws };
      const handler = makeHandler([PROCESS_NODE, "-e", TOKEN_PATH_DUMP], spawnEnv);
      const threadId = "11111111-2222-3333-4444-555555555555";

      const res = await handler.handle({
        jsonrpc: "2.0",
        method: "message/send",
        params: {
          message: {
            contextId: threadId,
            metadata: { mcpToken: "tok-A" },
            parts: [{ text: "ping" }],
          },
        },
        id: "iso-1",
      });

      const perThreadPath = resolvePerThreadMcpTokenPath(ws, threadId);
      assert.ok(perThreadPath);
      // The per-thread token file holds this turn's token.
      assert.equal(fs.readFileSync(perThreadPath, "utf8"), "tok-A");
      // The child was spawned pointing at exactly that file.
      const task = res.result as Record<string, unknown>;
      const artifacts = task["artifacts"] as Array<{ parts: Array<{ text: string }> }>;
      assert.equal(artifacts[0]?.parts[0]?.text, perThreadPath);
      // The shared path is still written, as a no-regression fallback.
      assert.equal(
        fs.readFileSync(path.join(ws, BRIDGE_STATE_DIR, MCP_TOKEN_FILE), "utf8"),
        "tok-A",
      );
      // The shared spawn env must NOT be mutated (concurrent turns share it).
      assert.equal(MCP_TOKEN_PATH_ENV_VAR in spawnEnv, false);
    } finally {
      fs.rmSync(ws, { recursive: true, force: true });
    }
  });

  it("isolates concurrent turns on different threads — no shared-file clobber", async () => {
    const ws = fs.mkdtempSync(path.join(os.tmpdir(), "sv-a2a-token-conc-"));
    try {
      const spawnEnv: NodeJS.ProcessEnv = { PATH: process.env.PATH, SPRING_WORKSPACE_PATH: ws };
      // Block briefly so the two turns genuinely overlap on the one shared
      // handler / spawnEnv — the exact condition the old single-file design
      // mishandled (last-writer-wins clobber + cross-thread 401).
      const blockingDump =
        "setTimeout(() => process.stdout.write(process.env.SPRING_MCP_TOKEN_PATH || 'UNSET'), 60)";
      const handler = makeHandler([PROCESS_NODE, "-e", blockingDump], spawnEnv);

      const threadA = "aaaaaaaa-0000-0000-0000-000000000000";
      const threadB = "bbbbbbbb-0000-0000-0000-000000000000";
      const send = (threadId: string, token: string, id: string) =>
        handler.handle({
          jsonrpc: "2.0",
          method: "message/send",
          params: {
            message: { contextId: threadId, metadata: { mcpToken: token }, parts: [{ text: "" }] },
          },
          id,
        });

      const [resA, resB] = await Promise.all([
        send(threadA, "token-A", "conc-A"),
        send(threadB, "token-B", "conc-B"),
      ]);

      const pathA = resolvePerThreadMcpTokenPath(ws, threadA);
      const pathB = resolvePerThreadMcpTokenPath(ws, threadB);
      assert.ok(pathA && pathB);
      // Each thread's file holds its own token — no last-writer-wins clobber.
      assert.equal(fs.readFileSync(pathA, "utf8"), "token-A");
      assert.equal(fs.readFileSync(pathB, "utf8"), "token-B");
      // Each child was pointed at its own per-thread file.
      const textOf = (res: { result?: unknown }) => {
        const task = res.result as Record<string, unknown>;
        const artifacts = task["artifacts"] as Array<{ parts: Array<{ text: string }> }>;
        return artifacts[0]?.parts[0]?.text;
      };
      assert.equal(textOf(resA), pathA);
      assert.equal(textOf(resB), pathB);
      // Neither turn mutated the shared spawn env.
      assert.equal(MCP_TOKEN_PATH_ENV_VAR in spawnEnv, false);
    } finally {
      fs.rmSync(ws, { recursive: true, force: true });
    }
  });
});
