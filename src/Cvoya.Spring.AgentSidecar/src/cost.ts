// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

// Parses the Claude Code CLI's `--output-format json` result so the bridge
// can (a) surface the assistant's prose as the A2A reply instead of a wall of
// JSON, and (b) hand the turn's cost + token usage back to the platform so the
// host-side cost ledger (`CostRecord`) and `BudgetEnforcer` actually receive
// real numbers (issue #3073).
//
// `claude --print --output-format json` emits a SINGLE JSON object — the
// terminal `result` message — with (the fields we care about):
//
//   {
//     "type": "result",
//     "subtype": "success",
//     "is_error": false,
//     "result": "the assistant's final text",
//     "total_cost_usd": 0.0123,
//     "usage": {
//       "input_tokens": 100,
//       "cache_creation_input_tokens": 0,
//       "cache_read_input_tokens": 0,
//       "output_tokens": 50
//     },
//     "modelUsage": { "claude-...": { ... } }
//   }
//
// This is scoped to the single-object JSON shape — i.e. a turn the launcher
// runs with `--output-format json`. The NDJSON stream-json shape (Gemini, and
// Claude's `--output-format stream-json --verbose`) is parsed by the richer
// `stream-json.ts` parser (#2226), which reuses this module's `TurnCost` and
// delegates the Claude terminal `result` event's cost extraction back to
// `parseCliJsonResult` (that event has the same fields as the single-object
// result), so there is exactly one Claude cost code-path.

/** Per-turn cost + usage the bridge surfaces to the host via A2A task metadata. */
export interface TurnCost {
  /** Total USD the CLI reported for the turn (`total_cost_usd`). Always > 0. */
  costUsd: number;
  /** Input tokens consumed, including cache-read / cache-creation tokens. */
  inputTokens: number;
  /** Output tokens generated. */
  outputTokens: number;
  /** Model id the CLI actually used, when discoverable; otherwise null. */
  model: string | null;
}

/** Outcome of parsing a CLI turn's stdout under `outputFormat: "json"`. */
export interface ParsedCliResult {
  /**
   * The assistant text to surface as the A2A reply. Falls back to the raw
   * stdout when the JSON could not be parsed, so a parser miss degrades to
   * the pre-#3073 behaviour (verbatim stdout) rather than dropping the reply.
   */
  reply: string;
  /** The turn's cost, or null when stdout was not parseable / carried no cost. */
  cost: TurnCost | null;
}

function asFiniteNumber(value: unknown): number | null {
  return typeof value === "number" && Number.isFinite(value) ? value : null;
}

function asNonNegativeInt(value: unknown): number {
  const n = asFiniteNumber(value);
  return n !== null && n >= 0 ? Math.trunc(n) : 0;
}

/**
 * Extracts the model id from the CLI result. Prefers the first key of
 * `modelUsage` (the model the CLI actually billed); falls back to a top-level
 * `model` string when present. Returns null when neither is available — the
 * host then fills the model from the agent definition.
 */
function extractModel(obj: Record<string, unknown>): string | null {
  const modelUsage = obj["modelUsage"];
  if (modelUsage && typeof modelUsage === "object") {
    const first = Object.keys(modelUsage as Record<string, unknown>)[0];
    if (first !== undefined && first.length > 0) {
      return first;
    }
  }
  const model = obj["model"];
  return typeof model === "string" && model.length > 0 ? model : null;
}

/**
 * Parses Claude Code `--output-format json` stdout into a reply string and an
 * optional {@link TurnCost}. Never throws: any malformed / non-JSON input
 * yields `{ reply: <raw stdout>, cost: null }` so the turn still returns the
 * CLI's output verbatim.
 */
export function parseCliJsonResult(stdout: string): ParsedCliResult {
  const trimmed = stdout.trim();
  if (trimmed.length === 0) {
    return { reply: stdout, cost: null };
  }

  let parsed: unknown;
  try {
    parsed = JSON.parse(trimmed);
  } catch {
    // Not JSON — hand back the raw stdout untouched.
    return { reply: stdout, cost: null };
  }

  if (parsed === null || typeof parsed !== "object" || Array.isArray(parsed)) {
    return { reply: stdout, cost: null };
  }

  const obj = parsed as Record<string, unknown>;

  // `result` is the assistant's final text. When it's missing (an error-shaped
  // result, or an unexpected schema) fall back to the raw stdout so the caller
  // still sees *something*.
  const result = obj["result"];
  const reply = typeof result === "string" ? result : stdout;

  const costUsd = asFiniteNumber(obj["total_cost_usd"]);
  if (costUsd === null || costUsd <= 0) {
    // No usable cost on this turn (e.g. a cached / zero-cost result). Surface
    // the reply but emit no cost signal — the host treats absence as "free".
    return { reply, cost: null };
  }

  const usage =
    obj["usage"] && typeof obj["usage"] === "object"
      ? (obj["usage"] as Record<string, unknown>)
      : {};

  const inputTokens =
    asNonNegativeInt(usage["input_tokens"]) +
    asNonNegativeInt(usage["cache_creation_input_tokens"]) +
    asNonNegativeInt(usage["cache_read_input_tokens"]);
  const outputTokens = asNonNegativeInt(usage["output_tokens"]);

  return {
    reply,
    cost: {
      costUsd,
      inputTokens,
      outputTokens,
      model: extractModel(obj),
    },
  };
}
