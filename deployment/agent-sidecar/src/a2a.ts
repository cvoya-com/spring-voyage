// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

// A2A 0.3.x JSON-RPC handlers. Mirrors the wire shape produced by the
// .NET A2AClient on the dispatcher side (see
// `src/Cvoya.Spring.Dapr/Execution/A2AExecutionDispatcher.cs`).
//
// Only the methods the dispatcher actually calls are implemented:
// `message/send`, `tasks/cancel`, `tasks/get`. Anything else returns
// JSON-RPC `-32601`.

import { randomUUID } from "node:crypto";

import { runAgentBridge } from "./bridge.js";
import { A2A_PROTOCOL_VERSION, BRIDGE_VERSION } from "./version.js";

export type TaskState =
  | "submitted"
  | "working"
  | "input-required"
  | "completed"
  | "canceled"
  | "failed";

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
    return {
      jsonrpc: "2.0",
      id,
      result: this.buildTaskResponse(taskId, task, []),
    };
  }

  private buildTaskResponse(taskId: string, task: ActiveTask, stderrLines: string[]) {
    const response: Record<string, unknown> = {
      id: taskId,
      status: {
        state: task.state,
        timestamp: new Date().toISOString(),
      },
      // Surface the bridge version inside the task payload too so a
      // dispatcher that doesn't read response headers still sees the
      // skew signal. Mirrors the Agent Card field.
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
      (response.status as Record<string, unknown>).message = {
        role: "agent",
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
