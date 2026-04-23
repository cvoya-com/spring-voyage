// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

import { strict as assert } from "node:assert";
import { describe, it } from "node:test";

import { A2AHandler } from "../src/a2a.ts";
import { A2A_PROTOCOL_VERSION, BRIDGE_VERSION } from "../src/version.ts";

const PROCESS_NODE = process.execPath;

function makeHandler(argv: string[]) {
  return new A2AHandler({
    agentName: "test-agent",
    agentArgv: argv,
    port: 8999,
    cancelGraceMs: 200,
    spawnEnv: process.env,
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

  it("round-trips a successful message/send to a stub CLI", async () => {
    // Wire shape (issue #1115): `message/send` returns
    // `{ result: { task: AgentTask } }` with proto-style enum names so
    // the .NET A2A SDK's `SendMessageResponse` deserializer (which
    // discriminates on field-presence between `task` and `message`)
    // picks up the AgentTask without throwing on `task.status.state`.
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
    const result = res.result as Record<string, unknown>;
    assert.ok(result, "expected a result payload");
    const taskWrapper = result.task as Record<string, unknown>;
    assert.ok(taskWrapper, "message/send result must be wrapped under `task` (SendMessageResponse contract)");
    const status = taskWrapper.status as Record<string, unknown>;
    // Proto-style enum name pinned by `A2A.TaskState` in the .NET SDK.
    // See https://github.com/a2aproject/a2a-dotnet/blob/main/src/A2A/Models/TaskState.cs.
    assert.equal(status.state, "TASK_STATE_COMPLETED");
    // contextId is [JsonRequired] on A2A.AgentTask; the bridge mirrors
    // the task id since it has no separate conversation handle.
    assert.equal(typeof taskWrapper.contextId, "string");
    const artifacts = taskWrapper.artifacts as Array<{ artifactId: string; parts: Array<{ text: string }> }>;
    assert.equal(artifacts.length, 1);
    assert.equal(artifacts[0]?.parts[0]?.text, "echo:ping");
    assert.equal((taskWrapper as Record<string, unknown>)["x-spring-voyage-bridge-version"], BRIDGE_VERSION);
  });

  it("reports failed state with stderr text on non-zero CLI exit", async () => {
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
    const result = res.result as Record<string, unknown>;
    const taskWrapper = result.task as Record<string, unknown>;
    const status = taskWrapper.status as Record<string, unknown>;
    assert.equal(status.state, "TASK_STATE_FAILED");
    const message = status.message as { role: string; messageId: string; parts: Array<{ text: string }> };
    // role + messageId are [JsonRequired] on A2A.Message; the bridge
    // emits the proto-style `ROLE_AGENT` and a fresh per-error
    // messageId because the SDK rejects either field being missing.
    assert.equal(message.role, "ROLE_AGENT");
    assert.equal(typeof message.messageId, "string");
    assert.match(message.parts[0]?.text ?? "", /boom/);
  });

  it("tasks/get returns the cached terminal state for a completed task", async () => {
    // Kick off a successful send first, capture the task id from the
    // response, then assert tasks/get returns the same state without
    // re-running the CLI. Note: tasks/get returns the bare AgentTask
    // (no `task` wrapper) because the dispatcher's GetTaskAsync
    // deserializes the result as `AgentTask` directly.
    const handler = makeHandler([PROCESS_NODE, "-e", "process.stdout.write('done')"]);
    const sendRes = await handler.handle({
      jsonrpc: "2.0",
      method: "message/send",
      params: { message: { parts: [{ text: "" }] } },
      id: 1,
    });
    const sendTask = (sendRes.result as { task: { id: string; status: { state: string } } }).task;
    assert.equal(sendTask.status.state, "TASK_STATE_COMPLETED");

    const getRes = await handler.handle({
      jsonrpc: "2.0",
      method: "tasks/get",
      params: { id: sendTask.id },
      id: 2,
    });
    const getResult = getRes.result as { id: string; status: { state: string } };
    assert.equal(getResult.id, sendTask.id);
    assert.equal(getResult.status.state, "TASK_STATE_COMPLETED");
  });

  it("tasks/cancel after terminal completion returns the cached state without re-running", async () => {
    const handler = makeHandler([PROCESS_NODE, "-e", "process.stdout.write('ok')"]);
    const sendRes = await handler.handle({
      jsonrpc: "2.0",
      method: "message/send",
      params: { message: { parts: [{ text: "" }] } },
      id: 1,
    });
    const sendTask = (sendRes.result as { task: { id: string; status: { state: string } } }).task;
    const cancelRes = await handler.handle({
      jsonrpc: "2.0",
      method: "tasks/cancel",
      params: { id: sendTask.id },
      id: 2,
    });
    // tasks/cancel also returns the bare AgentTask (no `task`
    // wrapper) — see CancelTaskAsync<AgentTask> on the dispatcher side.
    const cancelResult = cancelRes.result as { status: { state: string } };
    // Already completed → cancel must not flip the state.
    assert.equal(cancelResult.status.state, "TASK_STATE_COMPLETED");
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
});
