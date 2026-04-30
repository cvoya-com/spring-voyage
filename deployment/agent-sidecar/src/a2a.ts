// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

// A2A 0.3.x JSON-RPC handlers. Mirrors the wire shape consumed by the
// .NET A2A SDK that the dispatcher uses (see
// `src/Cvoya.Spring.Dapr/Execution/A2AExecutionDispatcher.cs` and the
// `A2A.V0_3` package on NuGet).
//
// Only the methods the dispatcher actually calls are implemented:
// `message/send`, `tasks/cancel`, `tasks/get`. Anything else returns
// JSON-RPC `-32601`.
//
// Wire-contract notes (issue #1198):
//
//   * Enum-valued fields use kebab-case-lower wire values matching the
//     .NET A2A V0_3 SDK's `KebabCaseLowerJsonStringEnumConverter`:
//       TaskState:   "submitted" | "working" | "input-required" |
//                    "completed" | "canceled" | "failed"
//       MessageRole: "user" | "agent"
//   * Every `AgentTask` on the wire carries a top-level `kind: "task"`
//     discriminator so the .NET SDK's `A2AEventConverterViaKindDiscriminator`
//     can deserialize it — both for `message/send` (which returns A2AResponse)
//     and for `tasks/get` / `tasks/cancel` (which return AgentTask directly
//     but still require the `kind` field per [JsonRequired]).
//   * `message/send` result is the flat `AgentTask` (with `kind: "task"`),
//     NOT wrapped under a `task` key. The V0_3 SDK's `SendMessageAsync`
//     reads `result` as `A2AResponse` using the kind discriminator.
//   * `Part` objects carry a `kind` discriminator: `"text"`, `"file"`,
//     or `"data"`. The bridge only emits `"text"` parts.
//   * Status `message` (AgentMessage embedded in TaskStatus) also carries
//     `kind: "message"` per the SDK's polymorphic serialization.

import { randomUUID } from "node:crypto";

import { runAgentBridge } from "./bridge.js";
import { A2A_PROTOCOL_VERSION, BRIDGE_VERSION } from "./version.js";

/**
 * A2A `TaskState` values, in the kebab-case-lower wire form required by the
 * .NET A2A V0_3 SDK's `KebabCaseLowerJsonStringEnumConverter`. These map
 * directly to the `TaskState` enum members on the .NET side:
 *   Submitted → "submitted", Working → "working",
 *   InputRequired → "input-required", Completed → "completed",
 *   Canceled → "canceled", Failed → "failed".
 *
 * The bridge only ever transitions through a subset of these states; all
 * values are typed here for completeness and future extension.
 */
export type TaskState =
  | "submitted"
  | "working"
  | "input-required"
  | "completed"
  | "canceled"
  | "failed";

/**
 * A2A `MessageRole` values, in the kebab-case-lower wire form required by
 * the .NET A2A V0_3 SDK. The bridge emits `"agent"` on the
 * `status.message` it attaches when surfacing CLI errors; `"user"` is
 * included for completeness.
 */
export type MessageRole = "user" | "agent";

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
      state: "working",
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
      task.state = "failed";
      task.errorMessage = (err as Error).message;
      this.tasks.set(taskId, task);
      // A2A v0.3: message/send result is the flat AgentTask (kind: "task")
      // consumed by the .NET SDK's SendMessageAsync as A2AResponse via the
      // kind discriminator. No "task" wrapper — the AgentTask itself is the
      // result, identified by its top-level "kind" field. (#1198)
      return {
        jsonrpc: "2.0",
        id,
        result: this.buildTaskResponse(taskId, task, stderrLines),
      };
    }

    if (result.cancelled) {
      task.state = "canceled";
    } else if (result.exitCode === 0) {
      task.state = "completed";
      task.outputArtifact = result.stdout;
    } else {
      task.state = "failed";
      task.errorMessage =
        result.stderr.length > 0
          ? result.stderr
          : `Agent CLI exited with code ${result.exitCode} and produced no stderr.`;
    }
    this.tasks.set(taskId, task);
    return {
      jsonrpc: "2.0",
      id,
      result: this.buildTaskResponse(taskId, task, stderrLines),
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
    if (task.state === "working") {
      task.abort.abort();
      task.state = "canceled";
      this.tasks.set(taskId, task);
    }
    // A2A v0.3: tasks/cancel result is the AgentTask with kind: "task".
    // The .NET SDK's CancelTaskAsync deserializes the result as AgentTask
    // directly; AgentTask is [JsonRequired] for "kind" so it must be present.
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
    // A2A v0.3: tasks/get result is the AgentTask with kind: "task".
    // The .NET SDK's GetTaskAsync deserializes the result as AgentTask
    // directly; AgentTask is [JsonRequired] for "kind" so it must be present.
    return {
      jsonrpc: "2.0",
      id,
      result: this.buildTaskResponse(taskId, task, []),
    };
  }

  /**
   * Builds the `AgentTask` payload that backs every terminal response.
   *
   * Wire shape per A2A v0.3 spec and the .NET `A2A.V0_3` SDK:
   *
   * - Top-level `kind: "task"` discriminator — required by both
   *   `A2AEventConverterViaKindDiscriminator` (for `message/send`) and the
   *   `[JsonRequired]` attribute on `AgentTask.Kind` (for `tasks/get` /
   *   `tasks/cancel`).
   * - `status.state` kebab-case-lower (e.g. `"completed"`, `"failed"`) —
   *   matches `KebabCaseLowerJsonStringEnumConverter` on `TaskState`.
   * - `status.message.role` kebab-case-lower (`"agent"`) — matches the same
   *   converter on `MessageRole`.
   * - `status.message.kind: "message"` — required by `AgentMessage`'s own
   *   `[JsonRequired]` when serialized as part of `TaskStatus.Message`.
   * - `artifacts[*].parts[*].kind: "text"` — required by the `Part`
   *   kind-discriminator converter (`PartConverterViaKindDiscriminator`).
   *
   * See `A2AExecutionDispatcher.MapA2AResponseToMessage` and the
   * `A2A.V0_3` SDK model source for the canonical required fields.
   */
  private buildTaskResponse(taskId: string, task: ActiveTask, stderrLines: string[]) {
    const response: Record<string, unknown> = {
      // A2A v0.3 kind discriminator — [JsonRequired] on AgentTask (#1198).
      kind: "task",
      id: taskId,
      // contextId is `[JsonRequired]` on `A2A.V0_3.AgentTask`. The bridge
      // doesn't have a real conversation handle to thread through here,
      // so we mirror the per-task id; the dispatcher only inspects
      // status / artifacts on the way back, never the contextId.
      contextId: taskId,
      status: {
        state: task.state satisfies TaskState,
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
        // A2A v0.3: Part objects carry a "kind" discriminator.
        // The bridge only produces text parts; "text" maps to TextPart
        // on the .NET SDK side.
        parts: [{ kind: "text", text: task.outputArtifact }],
      });
    }
    if (task.errorMessage !== null) {
      // AgentMessage embedded in TaskStatus.Message is also polymorphic
      // in the V0_3 SDK and serialized with kind: "message". Role and
      // MessageId are [JsonRequired]; we mint a fresh messageId per
      // status message because the bridge has no inbound id to echo here.
      (response.status as Record<string, unknown>).message = {
        kind: "message",
        role: "agent" satisfies MessageRole,
        messageId: randomUUID(),
        parts: [{ kind: "text", text: task.errorMessage }],
      };
    }
    if (stderrLines.length > 0) {
      // Captured stderr is informational; we tag it with a known
      // artifactId so consumers can distinguish it from stdout.
      artifacts.push({
        artifactId: "stderr",
        parts: [{ kind: "text", text: stderrLines.join("\n") }],
      });
    }
    if (artifacts.length > 0) {
      response.artifacts = artifacts;
    }
    return response;
  }
}
