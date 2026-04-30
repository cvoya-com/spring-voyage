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
});
