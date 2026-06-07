// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

// Parses the newline-delimited JSON (NDJSON) "stream-json" output the Claude
// Code and Gemini CLIs emit, so the A2A sidecar surfaces the assistant's reply
// — plus tool-use status and the turn's cost/usage — instead of storing the
// raw NDJSON wall as a single artifact (issue #2226).
//
// Two upstream schemas are handled. They share a `{"type":"result", ...}`
// terminal event but otherwise diverge, so the parser is shape-driven (it
// inspects each event's fields) rather than keyed on a per-launcher schema
// hint — the bridge already passes `SPRING_AGENT_OUTPUT_FORMAT=stream-json`
// (the format), and one parser covering both keeps the bridge agent-agnostic.
//
// Claude Code (`claude --print --output-format stream-json --verbose`,
// verified against claude-code 2.1.x):
//
//   {"type":"system","subtype":"init", ...}
//   {"type":"assistant","message":{"content":[{"type":"text","text":"..."},
//                                              {"type":"tool_use","name":"Bash", ...}]}, ...}
//   {"type":"result","subtype":"success","result":"<final assistant text>",
//    "total_cost_usd":0.07,"usage":{...},"modelUsage":{...}}
//
//   The terminal `result` event carries the SAME fields the single-object
//   `--output-format json` result does, so its cost/usage extraction is
//   delegated to `parseCliJsonResult` (cost.ts) — one cost code-path for both
//   Claude output shapes. The `result.result` string is the authoritative
//   final reply.
//
// Gemini (`gemini --output-format stream-json`, verified against
// gemini-cli 0.44.x):
//
//   {"type":"init","session_id":"...","model":"..."}
//   {"type":"message","role":"user","content":"..."}
//   {"type":"message","role":"assistant","content":"hello","delta":true}
//   {"type":"result","status":"success",
//    "stats":{"total_tokens":...,"input_tokens":...,"output_tokens":...,
//             "models":{"<model>":{...}}}}
//
//   Gemini's terminal `result` has NO `result` text field and NO USD cost —
//   only token `stats`. The reply is reconstructed by concatenating the
//   assistant `message` events' `content`; cost is token-only (costUsd: 0).

import { parseCliJsonResult, type TurnCost } from "./cost.js";

/** A tool call surfaced from the stream, for A2A status updates (#2226 step 3). */
export interface StreamToolCall {
  /** Tool name as the CLI reported it (e.g. `Bash`, a `run_shell_command`). */
  name: string;
}

/** Outcome of parsing an NDJSON stream-json turn's stdout. */
export interface ParsedStreamResult {
  /**
   * The assistant text to surface as the A2A reply. Falls back to the raw
   * stdout when no assistant text and no terminal `result` text were found,
   * so a parser miss degrades to the pre-#2226 behaviour (verbatim stdout)
   * rather than dropping the reply.
   */
  reply: string;
  /** The turn's cost, or null when the stream carried no usable cost. */
  cost: TurnCost | null;
  /** Tool calls observed in the stream, in order, for status surfacing. */
  toolCalls: StreamToolCall[];
  /**
   * True when a terminal `{"type":"result", ...}` event reported a failure
   * (`is_error: true` for Claude, `status` other than `"success"` for Gemini).
   * The caller maps this to A2A task failure. False when no error was seen.
   */
  isError: boolean;
  /** Error text from a failed terminal result, when the schema carried one. */
  errorMessage: string | null;
}

function asString(value: unknown): string | null {
  return typeof value === "string" && value.length > 0 ? value : null;
}

function asRecord(value: unknown): Record<string, unknown> | null {
  return value !== null && typeof value === "object" && !Array.isArray(value)
    ? (value as Record<string, unknown>)
    : null;
}

/**
 * Splits NDJSON stdout into the JSON events it parses cleanly. Blank lines and
 * non-JSON lines (a CLI may interleave a stray log line) are skipped rather
 * than failing the whole turn — robustness mirrors `parseCliJsonResult`.
 */
function parseEvents(stdout: string): Record<string, unknown>[] {
  const events: Record<string, unknown>[] = [];
  for (const rawLine of stdout.split("\n")) {
    const line = rawLine.trim();
    if (line.length === 0) {
      continue;
    }
    let parsed: unknown;
    try {
      parsed = JSON.parse(line);
    } catch {
      continue;
    }
    const rec = asRecord(parsed);
    if (rec !== null) {
      events.push(rec);
    }
  }
  return events;
}

/**
 * Pulls assistant text + tool calls out of one Claude `assistant` event's
 * `message.content` block array. Claude content blocks are
 * `{type:"text",text}` or `{type:"tool_use",name,...}` (other block types are
 * ignored). Returns the concatenated text and any tool-call names found.
 */
function readClaudeAssistantEvent(event: Record<string, unknown>): {
  text: string;
  toolCalls: StreamToolCall[];
} {
  const message = asRecord(event["message"]);
  const content = message ? message["content"] : undefined;
  let text = "";
  const toolCalls: StreamToolCall[] = [];
  if (Array.isArray(content)) {
    for (const block of content) {
      const rec = asRecord(block);
      if (rec === null) {
        continue;
      }
      if (rec["type"] === "text") {
        const t = asString(rec["text"]);
        if (t !== null) {
          text += t;
        }
      } else if (rec["type"] === "tool_use") {
        const name = asString(rec["name"]);
        if (name !== null) {
          toolCalls.push({ name });
        }
      }
    }
  }
  return { text, toolCalls };
}

/**
 * Parses NDJSON `stream-json` stdout (Claude Code or Gemini) into a reply, an
 * optional {@link TurnCost}, and the tool calls observed. Never throws: any
 * malformed / empty input yields `{ reply: <raw stdout>, cost: null,
 * toolCalls: [], isError: false }` so the turn still returns the CLI's output.
 */
export function parseStreamJson(stdout: string): ParsedStreamResult {
  const empty: ParsedStreamResult = {
    reply: stdout,
    cost: null,
    toolCalls: [],
    isError: false,
    errorMessage: null,
  };
  if (stdout.trim().length === 0) {
    return empty;
  }

  const events = parseEvents(stdout);
  if (events.length === 0) {
    // No JSON events at all — not a stream-json payload. Hand back raw stdout.
    return empty;
  }

  let assistantText = "";
  const toolCalls: StreamToolCall[] = [];
  let cost: TurnCost | null = null;
  let resultText: string | null = null;
  let isError = false;
  let errorMessage: string | null = null;

  for (const event of events) {
    const type = event["type"];

    // Claude assistant message: text + tool_use blocks under message.content.
    if (type === "assistant") {
      const { text, toolCalls: calls } = readClaudeAssistantEvent(event);
      assistantText += text;
      toolCalls.push(...calls);
      continue;
    }

    // Gemini message event: role-tagged, content is a plain string. Only the
    // assistant role contributes to the reply (user/tool roles are echoes).
    if (type === "message" && event["role"] === "assistant") {
      const content = asString(event["content"]);
      if (content !== null) {
        assistantText += content;
      }
      continue;
    }

    // Gemini tool-call event: a standalone `tool_use` / `tool_call` event
    // (distinct from Claude's inline content block). Surface the name when one
    // is present so the caller can emit a status update.
    if (type === "tool_use" || type === "tool_call") {
      const name = asString(event["name"]) ?? asString(event["tool"]);
      if (name !== null) {
        toolCalls.push({ name });
      }
      continue;
    }

    // Terminal result event (both schemas). Claude carries the authoritative
    // reply text + cost in the same shape parseCliJsonResult handles; Gemini
    // carries token stats only.
    if (type === "result") {
      // Claude error signal.
      if (event["is_error"] === true) {
        isError = true;
        errorMessage = asString(event["error"]) ?? asString(event["result"]);
      }
      // Gemini error signal: a `status` field that is not "success".
      const status = asString(event["status"]);
      if (status !== null && status !== "success") {
        isError = true;
        errorMessage = errorMessage ?? asString(event["error"]) ?? status;
      }

      // Claude: delegate to the single-object cost parser. It extracts the
      // `.result` reply text and `total_cost_usd` / `usage` / `modelUsage`.
      const claudeParsed = parseCliJsonResult(JSON.stringify(event));
      if (claudeParsed.cost !== null) {
        cost = claudeParsed.cost;
      }
      // Only adopt the parser's reply when the event actually had a `.result`
      // string (parseCliJsonResult falls back to its raw input otherwise — we
      // don't want the JSON-stringified event as the reply).
      if (typeof event["result"] === "string" && event["result"].length > 0) {
        resultText = event["result"];
      }

      // Gemini: synthesize a token-only cost from `stats` when no USD cost was
      // found (Gemini reports no `total_cost_usd`).
      if (cost === null) {
        const geminiCost = readGeminiResultStats(event);
        if (geminiCost !== null) {
          cost = geminiCost;
        }
      }
      continue;
    }
  }

  // Reply precedence: a terminal `result` text (Claude's authoritative final
  // text) wins; else the concatenated assistant message text (Gemini, or a
  // Claude stream truncated before its result event); else raw stdout so we
  // never drop output.
  const reply =
    resultText ?? (assistantText.length > 0 ? assistantText : stdout);

  return { reply, cost, toolCalls, isError, errorMessage };
}

function asNonNegativeInt(value: unknown): number {
  return typeof value === "number" && Number.isFinite(value) && value >= 0
    ? Math.trunc(value)
    : 0;
}

/**
 * Builds a token-only {@link TurnCost} from a Gemini terminal `result` event's
 * `stats`. Gemini reports no per-turn USD cost, so `costUsd` is 0 — the host
 * treats the absence of a positive cost as "free" but still records token
 * usage. Returns null when no usable token stats are present.
 */
function readGeminiResultStats(event: Record<string, unknown>): TurnCost | null {
  const stats = asRecord(event["stats"]);
  if (stats === null) {
    return null;
  }
  const inputTokens = asNonNegativeInt(stats["input_tokens"]);
  const outputTokens = asNonNegativeInt(stats["output_tokens"]);
  if (inputTokens === 0 && outputTokens === 0) {
    return null;
  }
  // Prefer the first key of `stats.models` as the model id, mirroring the
  // modelUsage convention in cost.ts.
  let model: string | null = null;
  const models = asRecord(stats["models"]);
  if (models !== null) {
    const first = Object.keys(models)[0];
    if (first !== undefined && first.length > 0) {
      model = first;
    }
  }
  return { costUsd: 0, inputTokens, outputTokens, model };
}
