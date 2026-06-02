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

import type { BootstrapFetcher } from "./bootstrap.js";
import { runAgentBridge } from "./bridge.js";
import type { ThreadBindingConfig } from "./config.js";
import {
  MCP_TOKEN_PATH_ENV_VAR,
  resolveMcpTokenPath,
  resolvePerThreadMcpTokenPath,
  writeMcpToken,
} from "./mcp-token-store.js";
import { ThreadIdRegistry } from "./threads.js";
import { A2A_PROTOCOL_VERSION, BRIDGE_VERSION } from "./version.js";

// ADR-0054 §5 / ADR-0057 §3: the worker-side dispatcher delivers the
// per-turn MCP session token in the A2A `message/send` metadata under
// this field. The long-running sidecar writes it to the workspace-
// resident MCP token file before spawning the CLI; the CLI's per-turn
// sidecar-MCP-server-mode child reads it from there.
const MCP_TOKEN_FIELD = "mcpToken";

// Per-member workspace mount path, stamped by the dispatcher (see
// AgentWorkspaceContract.WorkspacePathEnvVar). The bridge spawns the CLI
// in this directory so config files materialised at the workspace root
// (e.g. Claude Code's `.mcp.json`, CLAUDE.md) are discovered relative to
// the spawn CWD. When unset, spawn falls back to the bridge's own CWD
// (the image's WORKDIR), which is wrong for any launcher that relies on
// CWD-relative config discovery.
const WORKSPACE_PATH_ENV_VAR = "SPRING_WORKSPACE_PATH";

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
  // ADR-0041: how to deliver `thread.id` to the spawned CLI as its
  // session identifier. Absent when the wrapped runtime has no session
  // concept (or carries the id via env var rather than argv). When set,
  // the handler appends `[createArg|resumeArg, threadId]` to argv on
  // every `message/send`, picking create vs resume by whether it has
  // already seen the same thread id (per-process + persisted to the
  // workspace volume — see `ThreadIdRegistry`).
  threadBinding?: ThreadBindingConfig;
  // Tracks which thread ids the bridge has dispatched before. When
  // omitted the handler constructs an in-memory registry (no
  // persistence — fine for tests, lossy on container restart in prod).
  // Inject explicitly to share state across handler instances or to
  // pin a workspace location.
  threadIdRegistry?: ThreadIdRegistry;
  // ADR-0055 §6: pre-spawn integrity check for the platform-authoritative
  // workspace files. When set, the handler runs
  // `integrityCheckAndRefresh()` before every CLI spawn — alongside the
  // existing per-turn MCP-token rewrite — so a turn that mutated
  // `CLAUDE.md` or `.mcp.json` is re-baselined before the next exec.
  // Null when the launcher did not stamp SPRING_BOOTSTRAP_URL (the
  // pre-pull behaviour, dormant in Wave 2).
  bootstrapFetcher?: BootstrapFetcher | null;
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
  // ADR-0041: tracks which thread ids this bridge has dispatched before.
  // First send for a thread uses `createArg` (e.g. `claude --session-id
  // <id>` — mint a session with this id); subsequent sends use
  // `resumeArg` (`claude --resume <id>` — load the existing session
  // file). The registry persists to the per-agent workspace volume so
  // the answer survives container restart (#2094 acceptance).
  private readonly threadIdRegistry: ThreadIdRegistry;

  constructor(deps: A2AHandlerDeps) {
    this.deps = deps;
    this.threadIdRegistry =
      deps.threadIdRegistry ?? new ThreadIdRegistry(undefined);
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

  /**
   * Dispatches a single JSON-RPC envelope. `requestSignal` is the
   * cancellation signal for the inbound HTTP request: when it aborts
   * (the dispatcher closed the connection before we replied), any
   * in-flight `message/send` work is torn down so the spawned CLI does
   * not outlive the dispatcher's view of the turn. Optional so direct
   * callers (tests, future non-HTTP transports) can omit it.
   *
   * #2718: this is the bridge half of the worker → agent "give up"
   * symmetry. The dispatcher half is the `finally` in
   * `A2AExecutionDispatcher.SendA2AMessageAsync` that calls
   * `tasks/cancel` on any non-terminal exit path.
   */
  async handle(
    req: JsonRpcRequest,
    requestSignal?: AbortSignal,
  ): Promise<JsonRpcResponse> {
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
        return this.handleSendMessage(req, id, requestSignal);
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

  /**
   * Extracts the A2A 0.3 `contextId` from `message/send` params. Per
   * ADR-0041 this is the platform's `thread.id` verbatim — the bridge
   * passes it straight through to the CLI as the session identifier.
   *
   * The .NET dispatcher
   * (`A2AExecutionDispatcher.MapA2AResponseToMessage` and the surrounding
   * `MessageSendParams` build) sets `params.message.contextId`. We look
   * there first and fall back to a top-level `params.contextId` for
   * generality (the A2A 0.3 spec puts it on the message, but some
   * mid-version clients put it on the envelope).
   */
  private extractContextId(params: unknown): string | undefined {
    if (!params || typeof params !== "object") {
      return undefined;
    }
    const root = params as Record<string, unknown>;
    const message = root["message"];
    if (message && typeof message === "object") {
      const fromMessage = (message as Record<string, unknown>)["contextId"];
      if (typeof fromMessage === "string" && fromMessage.length > 0) {
        return fromMessage;
      }
    }
    const fromRoot = root["contextId"];
    if (typeof fromRoot === "string" && fromRoot.length > 0) {
      return fromRoot;
    }
    return undefined;
  }

  /**
   * Returns the argv to spawn for a given thread id, given the
   * configured `ThreadBindingConfig`. First send for a thread appends
   * `[createArg, threadId]`; subsequent sends append `[resumeArg,
   * threadId]`. The seen-set is updated *after* the spawn invariant is
   * decided so the second send on the same thread within one process
   * gets the resume path even if the first is still in flight.
   *
   * Visible for testing. Does not mutate; the caller is responsible for
   * `threadIdRegistry.add(threadId)` after a successful spawn.
   */
  private composeArgv(
    threadId: string | undefined,
    binding: ThreadBindingConfig | undefined,
  ): string[] {
    if (!threadId || !binding) {
      return this.deps.agentArgv;
    }
    const flag = this.threadIdRegistry.has(threadId) ? binding.resumeArg : binding.createArg;
    return [...this.deps.agentArgv, flag, threadId];
  }

  /**
   * Extracts the per-turn MCP session token from `message/send` params.
   * ADR-0052 §4: the worker-side dispatcher places it on
   * `params.message.metadata.mcpToken`.
   */
  private extractMcpToken(params: unknown): string | undefined {
    if (!params || typeof params !== "object") {
      return undefined;
    }

    const message = (params as Record<string, unknown>)["message"];
    if (!message || typeof message !== "object") {
      return undefined;
    }

    const metadata = (message as Record<string, unknown>)["metadata"];
    if (!metadata || typeof metadata !== "object") {
      return undefined;
    }

    const metadataToken = (metadata as Record<string, unknown>)[MCP_TOKEN_FIELD];
    return typeof metadataToken === "string" && metadataToken.length > 0
      ? metadataToken
      : undefined;
  }

  /**
   * ADR-0057 §3 / #3000: delivers this turn's MCP session token to the
   * CLI's MCP-server-mode child, isolated per concurrent turn, and returns
   * the environment the CLI should be spawned with.
   *
   * Under `SPRING_CONCURRENT_THREADS=true` the long-running sidecar runs
   * multiple turns at once. A single shared token file races: a child can
   * read a sibling turn's token, which is revoked when that sibling turn
   * ends → 401 (and, before that, wrong-thread attribution, because the
   * token carries the turn's thread/message id). So we write the token to
   * a per-thread path and point the child at that exact file via
   * `MCP_TOKEN_PATH_ENV_VAR`, returned on a per-turn CLONE of the spawn
   * env — never mutating the shared `this.deps.spawnEnv`, which would
   * itself race across concurrent turns. The actor serialises turns within
   * a thread, so the per-thread file never races.
   *
   * The shared (legacy) path is always (re)written too, as a no-regression
   * fallback for runtimes that do not propagate the CLI env to the child,
   * and for turns with no thread id (`MCP_TOKEN_PATH_ENV_VAR` is then left
   * unset and the child resolves the shared path).
   *
   * No-ops on the token write when the message carried no `mcpToken` (the
   * previous turn's token, if any, is left in place — the worker rejects a
   * stale token as 401, which the model can retry) or when there is no
   * workspace mount to write to. A write failure is logged but never fails
   * the turn.
   */
  private prepareTurnEnv(
    mcpToken: string | undefined,
    threadId: string | undefined,
  ): NodeJS.ProcessEnv {
    const spawnEnv = this.deps.spawnEnv;
    if (!mcpToken) {
      return spawnEnv;
    }
    const workspacePath = spawnEnv[WORKSPACE_PATH_ENV_VAR];

    // No-regression fallback: always (re)write the shared per-agent path
    // so runtimes that don't propagate the CLI env to the child still find
    // a token at the path they resolve from SPRING_WORKSPACE_PATH.
    this.writeTokenFile(resolveMcpTokenPath(workspacePath), mcpToken);

    // Primary: per-thread-isolated path + an explicit pointer for the
    // child so concurrent turns never read each other's token.
    const perThreadPath = resolvePerThreadMcpTokenPath(workspacePath, threadId);
    if (perThreadPath === null || !this.writeTokenFile(perThreadPath, mcpToken)) {
      // No thread id, or the per-thread write failed: leave the child to
      // resolve the shared path (just written) rather than point it at a
      // path with no token.
      return spawnEnv;
    }
    return { ...spawnEnv, [MCP_TOKEN_PATH_ENV_VAR]: perThreadPath };
  }

  /**
   * Writes a token to a file via the atomic store helper, logging (never
   * throwing on) a write failure. Returns whether the token was written.
   */
  private writeTokenFile(tokenPath: string | null, token: string): boolean {
    if (tokenPath === null) {
      return false;
    }
    const result = writeMcpToken(tokenPath, token);
    if (result.warning) {
      process.stderr.write(
        `${JSON.stringify({
          ts: new Date().toISOString(),
          level: "warn",
          component: "spring-voyage-agent-sidecar",
          bridgeVersion: BRIDGE_VERSION,
          message: "per-turn MCP token write failed",
          detail: result.warning,
        })}\n`,
      );
    }
    return result.written;
  }

  /**
   * ADR-0055 §6: runs the bootstrap integrity check before each CLI
   * spawn. No-op when the sidecar was started without a fetcher (the
   * pre-pull path, dormant in Wave 2 until launchers stamp the env
   * vars). Failures are logged but never throw — a stale platform file
   * is preferable to taking down a turn.
   */
  private async runBootstrapIntegrityCheck(): Promise<void> {
    const fetcher = this.deps.bootstrapFetcher;
    if (!fetcher) {
      return;
    }
    let result;
    try {
      result = await fetcher.integrityCheckAndRefresh();
    } catch (err) {
      process.stderr.write(
        `${JSON.stringify({
          ts: new Date().toISOString(),
          level: "warn",
          component: "spring-voyage-agent-sidecar",
          bridgeVersion: BRIDGE_VERSION,
          message: "bootstrap integrity check threw",
          detail: (err as Error).message,
        })}\n`,
      );
      return;
    }
    if (result.warning) {
      process.stderr.write(
        `${JSON.stringify({
          ts: new Date().toISOString(),
          level: "warn",
          component: "spring-voyage-agent-sidecar",
          bridgeVersion: BRIDGE_VERSION,
          message: "bootstrap integrity check warning",
          detail: result.warning,
        })}\n`,
      );
      return;
    }
    if (result.restored && result.restored.length > 0) {
      process.stderr.write(
        `${JSON.stringify({
          ts: new Date().toISOString(),
          level: "info",
          component: "spring-voyage-agent-sidecar",
          bridgeVersion: BRIDGE_VERSION,
          message: "bootstrap integrity check restored files",
          restored: result.restored,
        })}\n`,
      );
    }
  }

  private async handleSendMessage(
    req: JsonRpcRequest,
    id: string | number | null,
    requestSignal?: AbortSignal,
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

    // #2718: when the dispatcher cancels its outbound HTTP call (HttpClient
    // timeout, actor turn cancellation, dispose-mid-flight), the inbound
    // request signal aborts. Propagate that to the per-task AbortController
    // so runAgentBridge SIGTERM/SIGKILLs the spawned CLI. Without this the
    // CLI keeps running after the dispatcher's `finally` revokes the MCP
    // session, producing the mid-turn 401 loop. The dispatcher's
    // `tasks/cancel` path is the symmetric, explicit fallback.
    let onRequestAbort: (() => void) | undefined;
    if (requestSignal) {
      if (requestSignal.aborted) {
        abort.abort();
      } else {
        onRequestAbort = () => abort.abort();
        requestSignal.addEventListener("abort", onRequestAbort, { once: true });
      }
    }

    const userText = this.extractText(req.params);
    const spawnEnv = this.deps.spawnEnv;
    const stderrLines: string[] = [];

    // ADR-0055 §6: pre-spawn integrity check on the platform-authoritative
    // workspace files. Any divergence triggers a refresh from the cached
    // bundle (or a re-fetch when the worker indicates the bundle changed).
    // Runs before the MCP-token rewrite because a refresh may rewrite
    // `.mcp.json` as part of restoring the bundle — the token rewrite
    // below then stamps the per-turn token on top.
    await this.runBootstrapIntegrityCheck();

    // ADR-0041 / #2094: `params.message.contextId` is the platform
    // thread.id. When the launcher declared a thread-binding (e.g.
    // Claude Code with --session-id / --resume), append the create or
    // resume flag + the id to argv on every spawn. Otherwise spawn the
    // launcher-supplied argv unchanged. Extracted before the token write
    // because the per-turn token file is keyed by thread id (#3000).
    const threadId = this.extractContextId(req.params);

    // ADR-0057 §3 / #3000: deliver this turn's MCP session token to the
    // CLI's MCP-server-mode child via a per-thread-isolated file, and get
    // back the per-turn spawn env (a clone pointing the child at that
    // file). Writing here, before the spawn below, guarantees the child
    // sees the current turn's token, not a sibling concurrent turn's.
    const turnEnv = this.prepareTurnEnv(this.extractMcpToken(req.params), threadId);

    const argv = this.composeArgv(threadId, this.deps.threadBinding);
    const spawnCwd = spawnEnv[WORKSPACE_PATH_ENV_VAR];

    let result;
    try {
      result = await runAgentBridge({
        argv,
        stdin: userText,
        env: turnEnv,
        cwd: spawnCwd,
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

    // ADR-0041 / #2094: If the create path fails with "Session ID … is already
    // in use", Claude wrote the session file in a prior run but the
    // seen-threads marker was never persisted (e.g. the bridge process crashed
    // between the CLI exit and the appendFileSync, or a webhook was redelivered
    // concurrently so two sends raced on the same thread id). Recover by
    // marking the thread as seen and retrying with --resume. The marker write
    // in the happy path below then keeps future sends on the resume path.
    if (
      result.exitCode !== 0 &&
      !result.cancelled &&
      threadId &&
      this.deps.threadBinding &&
      !this.threadIdRegistry.has(threadId) &&
      result.stderr.includes("is already in use")
    ) {
      this.threadIdRegistry.add(threadId);
      const retryArgv = this.composeArgv(threadId, this.deps.threadBinding);
      stderrLines.length = 0;
      try {
        result = await runAgentBridge({
          argv: retryArgv,
          stdin: userText,
          env: turnEnv,
          cwd: spawnCwd,
          signal: abort.signal,
          cancelGraceMs: this.deps.cancelGraceMs,
          onStderrLine: (line) => {
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

    // ADR-0041: only record the thread id after the CLI actually
    // accepted the create call. If the spawn failed (non-zero exit, ENOENT,
    // ...) we leave the thread id off the seen-set so the next message
    // retries the create path — the CLI didn't write a session file we
    // could resume from. The ordering matters: a record on
    // pre-spawn-failure would permanently break resume for that thread.
    if (threadId && this.deps.threadBinding && task.state === "completed") {
      this.threadIdRegistry.add(threadId);
    }
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
