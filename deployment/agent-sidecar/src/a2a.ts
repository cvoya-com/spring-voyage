// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

// A2A 0.3.x JSON-RPC handlers. Mirrors the wire shape consumed by the
// .NET A2A SDK that the dispatcher uses (see
// `src/Cvoya.Spring.Dapr/Execution/A2AExecutionDispatcher.cs` and the
// `a2a` package on NuGet).
//
// Only the methods the dispatcher actually calls are implemented:
// `message/send`, `tasks/cancel`, `tasks/get`. Anything else returns
// JSON-RPC `-32601`.
//
// Wire-contract notes (issue #1115):
//
//   * Enum-valued fields are encoded with the .NET A2A SDK's proto-style
//     names (`TASK_STATE_COMPLETED`, `ROLE_AGENT`, ...) — NOT the
//     lowercase A2A 0.3 spec strings. The .NET SDK pins the proto-style
//     names on every enum via `[JsonStringEnumMemberName]` (see
//     https://github.com/a2aproject/a2a-dotnet/blob/main/src/A2A/Models/TaskState.cs
//     and `Role.cs` in the same folder). The bridge picks the .NET-side
//     casing because the SDK is the wire-stable consumer; the spec
//     mentions both forms but the .NET SDK only accepts the proto form.
//   * `message/send` returns the `AgentTask` wrapped under a `task` key
//     (matching the .NET SDK's `SendMessageResponse` field-presence
//     shape: `{ task: AgentTask }` or `{ message: Message }`). The
//     `tasks/get` and `tasks/cancel` results are bare `AgentTask`
//     objects — those endpoints deserialize as `AgentTask` directly on
//     the dispatcher side, not via `SendMessageResponse`.

import { randomUUID } from "node:crypto";

import { runAgentBridge } from "./bridge.js";
import { A2A_PROTOCOL_VERSION, BRIDGE_VERSION } from "./version.js";

/**
 * A2A `TaskState` values, in the proto-style enum form expected by the
 * .NET A2A SDK. Mirrors `A2A.TaskState` in the SDK; only the values the
 * bridge actually emits are exposed here.
 *
 * The .NET SDK pins these strings via `[JsonStringEnumMemberName]`; see
 * https://github.com/a2aproject/a2a-dotnet/blob/main/src/A2A/Models/TaskState.cs.
 */
export type TaskState =
  | "TASK_STATE_SUBMITTED"
  | "TASK_STATE_WORKING"
  | "TASK_STATE_INPUT_REQUIRED"
  | "TASK_STATE_COMPLETED"
  | "TASK_STATE_CANCELED"
  | "TASK_STATE_FAILED";

/**
 * A2A `Role` values, in the proto-style enum form expected by the .NET
 * A2A SDK. The bridge currently only ever emits `ROLE_AGENT` (on the
 * `status.message` it attaches when surfacing CLI errors); user-side
 * messages flow in from the dispatcher and are not echoed back here.
 *
 * Mirrors `A2A.Role`:
 * https://github.com/a2aproject/a2a-dotnet/blob/main/src/A2A/Models/Role.cs.
 */
export type Role = "ROLE_USER" | "ROLE_AGENT";

export interface JsonRpcRequest {
  jsonrpc: "2.0";
  method: string;
  // unknown is the right shape; specific handlers cast as needed.
  params?: unknown;
  id?: string | number | null;
}

export interface JsonRpcResponse {
  jsonrpc: "2.0";
  id: string | number | null;
  result?: unknown;
  error?: { code: number; message: string; data?: unknown };
}

export interface AgentCard {
  name: string;
  description: string;
  protocolVersion: string;
  version: string;
  // Spring Voyage extension — also surfaced as a response header
  // so the dispatcher can log version skew without having to parse
  // the agent card on every call.
  "x-spring-voyage-bridge-version": string;
  capabilities: {
    streaming: boolean;
    pushNotifications: boolean;
  };
  skills: Array<{
    id: string;
    name: string;
    description: string;
  }>;
  interfaces: Array<{
    protocol: string;
    url: string;
  }>;
}

export interface A2AHandlerDeps {
  agentName: string;
  agentArgv: string[];
  port: number;
  cancelGraceMs: number;
  spawnEnv: NodeJS.ProcessEnv;
}

interface ActiveTask {
  abort: AbortController;
  state: TaskState;
  outputArtifact: string | null;
  errorMessage: string | null;
}

export class A2AHandler {
  private readonly deps: A2AHandlerDeps;
  private readonly tasks = new Map<string, ActiveTask>();

  constructor(deps: A2AHandlerDeps) {
    this.deps = deps;
  }

  buildAgentCard(): AgentCard {
    return {
      name: this.deps.agentName,
      description:
        `A2A 0.3.x bridge for the CLI command '${this.deps.agentArgv[0] ?? "<unset>"}'. ` +
        `Spawns the configured argv on every message/send, pipes the user prompt to stdin, ` +
        `and returns stdout as the agent response.`,
      protocolVersion: A2A_PROTOCOL_VERSION,
      version: BRIDGE_VERSION,
      "x-spring-voyage-bridge-version": BRIDGE_VERSION,
      capabilities: {
        streaming: false,
        pushNotifications: false,
      },
      skills: [
        {
          id: "execute",
          name: "Execute Task",
          description: "Sends the prompt body to the wrapped CLI on stdin and returns its stdout.",
        },
      ],
      interfaces: [
        {
          protocol: "jsonrpc/http",
          url: `http://localhost:${this.deps.port}/`,
        },
      ],
    };
  }

  async handle(req: JsonRpcRequest): Promise<JsonRpcResponse> {
    const id = req.id ?? null;
    if (req.jsonrpc !== "2.0" || typeof req.method !== "string") {
      return {
        jsonrpc: "2.0",
        id,
        error: { code: -32600, message: "Invalid JSON-RPC 2.0 request" },
      };
    }
    switch (req.method) {
      case "message/send":
        return this.handleSendMessage(req, id);
      case "tasks/cancel":
        return this.handleCancelTask(req, id);
      case "tasks/get":
        return this.handleGetTask(req, id);
      default:
        return {
          jsonrpc: "2.0",
          id,
          error: { code: -32601, message: `Method not found: ${req.method}` },
        };
    }
  }

  private extractText(params: unknown): string {
    if (!params || typeof params !== "object") {
      return "";
    }
    const message = (params as Record<string, unknown>)["message"];
    if (!message || typeof message !== "object") {
      return "";
    }
    const parts = (message as Record<string, unknown>)["parts"];
    if (!Array.isArray(parts)) {
      return "";
    }
    let text = "";
    for (const part of parts) {
      if (part && typeof part === "object" && typeof (part as Record<string, unknown>)["text"] === "string") {
        text += (part as Record<string, string>)["text"];
      }
    }
    return text;
  }

  private async handleSendMessage(
    req: JsonRpcRequest,
    id: string | number | null,
  ): Promise<JsonRpcResponse> {
    if (this.deps.agentArgv.length === 0) {
      return {
        jsonrpc: "2.0",
        id,
        error: {
          code: -32603,
          message:
            "Bridge mis-configured: SPRING_AGENT_ARGV is empty. The dispatcher must set it from AgentLaunchSpec.Argv.",
        },
      };
    }

    const taskId = randomUUID();
    const abort = new AbortController();
    const task: ActiveTask = {
      abort,
      state: "TASK_STATE_WORKING",
      outputArtifact: null,
      errorMessage: null,
    };
    this.tasks.set(taskId, task);

    const userText = this.extractText(req.params);
    const stderrLines: string[] = [];

    let result;
    try {
      result = await runAgentBridge({
        argv: this.deps.agentArgv,
        stdin: userText,
        env: this.deps.spawnEnv,
        signal: abort.signal,
        cancelGraceMs: this.deps.cancelGraceMs,
        onStderrLine: (line) => {
          // Best-effort: capture lines to surface as TaskStatusUpdate.
          // We currently fold them into the final task, since the
          // dispatcher's A2AClient consumes a unary response. Streaming
          // SSE delivery is a future optional capability.
          stderrLines.push(line);
        },
      });
    } catch (err) {
      task.state = "TASK_STATE_FAILED";
      task.errorMessage = (err as Error).message;
      this.tasks.set(taskId, task);
      return {
        jsonrpc: "2.0",
        id,
        // message/send result is `SendMessageResponse` on the .NET side,
        // a field-presence wrapper around either `task` or `message`.
        // Wrap the AgentTask under `task` so the dispatcher's
        // SendMessageResponse deserializer picks it up. (Without the
        // wrap, both `Task` and `Message` come back null and the
        // dispatcher silently maps the response to "No response from
        // A2A agent." — see #1115.)
        result: { task: this.buildTaskResponse(taskId, task, stderrLines) },
      };
    }

    if (result.cancelled) {
      task.state = "TASK_STATE_CANCELED";
    } else if (result.exitCode === 0) {
      task.state = "TASK_STATE_COMPLETED";
      task.outputArtifact = result.stdout;
    } else {
      task.state = "TASK_STATE_FAILED";
      task.errorMessage =
        result.stderr.length > 0
          ? result.stderr
          : `Agent CLI exited with code ${result.exitCode} and produced no stderr.`;
    }
    this.tasks.set(taskId, task);
    return {
      jsonrpc: "2.0",
      id,
      result: { task: this.buildTaskResponse(taskId, task, stderrLines) },
    };
  }

  private handleCancelTask(req: JsonRpcRequest, id: string | number | null): JsonRpcResponse {
    const params = (req.params ?? {}) as Record<string, unknown>;
    const taskId = typeof params["id"] === "string" ? (params["id"] as string) : null;
    if (!taskId) {
      return {
        jsonrpc: "2.0",
        id,
        error: { code: -32602, message: "tasks/cancel requires params.id" },
      };
    }
    const task = this.tasks.get(taskId);
    if (!task) {
      return {
        jsonrpc: "2.0",
        id,
        error: { code: -32001, message: `Task not found: ${taskId}` },
      };
    }
    if (task.state === "TASK_STATE_WORKING") {
      task.abort.abort();
      task.state = "TASK_STATE_CANCELED";
      this.tasks.set(taskId, task);
    }
    // tasks/cancel result deserializes as `AgentTask` directly on the
    // dispatcher side (A2AClient.CancelTaskAsync), so the result is the
    // bare AgentTask shape — no `task` wrapper here.
    return {
      jsonrpc: "2.0",
      id,
      result: this.buildTaskResponse(taskId, task, []),
    };
  }

  private handleGetTask(req: JsonRpcRequest, id: string | number | null): JsonRpcResponse {
    const params = (req.params ?? {}) as Record<string, unknown>;
    const taskId = typeof params["id"] === "string" ? (params["id"] as string) : null;
    if (!taskId) {
      return {
        jsonrpc: "2.0",
        id,
        error: { code: -32602, message: "tasks/get requires params.id" },
      };
    }
    const task = this.tasks.get(taskId);
    if (!task) {
      return {
        jsonrpc: "2.0",
        id,
        error: { code: -32001, message: `Task not found: ${taskId}` },
      };
    }
    // tasks/get result deserializes as `AgentTask` directly on the
    // dispatcher side (A2AClient.GetTaskAsync), so the result is the
    // bare AgentTask shape — no `task` wrapper here.
    return {
      jsonrpc: "2.0",
      id,
      result: this.buildTaskResponse(taskId, task, []),
    };
  }

  /**
   * Builds the `AgentTask` payload that backs every terminal response.
   * The shape mirrors `A2A.AgentTask` from the .NET SDK so the
   * dispatcher's `JsonStringEnumConverter`-driven deserializer accepts
   * it without throwing on `task.status.state`. See `A2A.AgentTask`,
   * `A2A.TaskStatus`, `A2A.TaskState`, `A2A.Message`, and `A2A.Role` in
   * https://github.com/a2aproject/a2a-dotnet/tree/main/src/A2A/Models
   * for the exact fields and required-vs-optional split.
   */
  private buildTaskResponse(taskId: string, task: ActiveTask, stderrLines: string[]) {
    const response: Record<string, unknown> = {
      id: taskId,
      // contextId is `[JsonRequired]` on `A2A.AgentTask`. The bridge
      // doesn't have a real conversation handle to thread through here,
      // so we mirror the per-task id; the dispatcher only inspects
      // status / artifacts on the way back, never the contextId.
      contextId: taskId,
      status: {
        state: task.state,
        timestamp: new Date().toISOString(),
      },
      // Surface the bridge version inside the task payload too so a
      // dispatcher that doesn't read response headers still sees the
      // skew signal. Mirrors the Agent Card field. (Extra keys on the
      // wire are ignored by the .NET SDK's deserializer.)
      "x-spring-voyage-bridge-version": BRIDGE_VERSION,
    };
    const artifacts: Array<Record<string, unknown>> = [];
    if (task.outputArtifact !== null && task.outputArtifact.length > 0) {
      artifacts.push({
        artifactId: randomUUID(),
        parts: [{ text: task.outputArtifact }],
      });
    }
    if (task.errorMessage !== null) {
      // `A2A.Message.Role`, `Parts`, and `MessageId` are all
      // `[JsonRequired]`. Role is the proto-style `ROLE_AGENT`; we mint
      // a fresh messageId per status message because the spec /
      // SDK make the field mandatory and the bridge has no notion of
      // an inbound id to echo here.
      (response.status as Record<string, unknown>).message = {
        role: "ROLE_AGENT" satisfies Role,
        messageId: randomUUID(),
        parts: [{ text: task.errorMessage }],
      };
    }
    if (stderrLines.length > 0) {
      // Captured stderr is informational; we tag it with a known
      // artifactId so consumers can distinguish it from stdout.
      artifacts.push({
        artifactId: "stderr",
        parts: [{ text: stderrLines.join("\n") }],
      });
    }
    if (artifacts.length > 0) {
      response.artifacts = artifacts;
    }
    return response;
  }
}
