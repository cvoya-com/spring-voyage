// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

// Tests for the NDJSON stream-json parser (#2226). The fixtures are reduced
// captures of the REAL output emitted by the pinned CLIs:
//   * Claude Code 2.1.x: `claude --print --output-format stream-json --verbose`
//   * Gemini CLI 0.44.x: `gemini --output-format stream-json`
// so the parser is pinned against the actual upstream schemas, not a guess.

import { strict as assert } from "node:assert";
import { describe, it } from "node:test";

import { parseStreamJson } from "../src/stream-json.ts";

// Joins event objects into an NDJSON payload the way the CLIs write it.
function ndjson(...events: unknown[]): string {
  return events.map((e) => JSON.stringify(e)).join("\n") + "\n";
}

describe("parseStreamJson — Claude Code stream-json", () => {
  it("extracts the terminal result's reply text and cost/usage", () => {
    const stdout = ndjson(
      { type: "system", subtype: "init", session_id: "s1", model: "claude-opus-4-8" },
      {
        type: "assistant",
        message: {
          model: "claude-opus-4-8",
          role: "assistant",
          content: [{ type: "text", text: "hi there" }],
          usage: { input_tokens: 6240, output_tokens: 1 },
        },
      },
      {
        type: "result",
        subtype: "success",
        is_error: false,
        result: "hi there",
        total_cost_usd: 0.07058375,
        usage: {
          input_tokens: 6240,
          cache_creation_input_tokens: 5055,
          cache_read_input_tokens: 15330,
          output_tokens: 5,
        },
        modelUsage: { "claude-opus-4-8": { costUSD: 0.07058375 } },
      },
    );

    const parsed = parseStreamJson(stdout);

    // The terminal result's `.result` is the authoritative reply, NOT the JSON wall.
    assert.equal(parsed.reply, "hi there");
    assert.ok(parsed.cost);
    assert.equal(parsed.cost!.costUsd, 0.07058375);
    // input tokens fold in cache-creation + cache-read (parseCliJsonResult contract).
    assert.equal(parsed.cost!.inputTokens, 6240 + 5055 + 15330);
    assert.equal(parsed.cost!.outputTokens, 5);
    assert.equal(parsed.cost!.model, "claude-opus-4-8");
    assert.equal(parsed.isError, false);
  });

  it("surfaces tool_use content blocks as tool calls", () => {
    const stdout = ndjson(
      { type: "system", subtype: "init", session_id: "s1" },
      {
        type: "assistant",
        message: {
          role: "assistant",
          content: [
            { type: "text", text: "Let me check." },
            { type: "tool_use", id: "tu_1", name: "Bash", input: { command: "ls" } },
          ],
        },
      },
      {
        type: "assistant",
        message: { role: "assistant", content: [{ type: "tool_use", name: "Read" }] },
      },
      { type: "result", subtype: "success", is_error: false, result: "done" },
    );

    const parsed = parseStreamJson(stdout);

    assert.equal(parsed.reply, "done");
    assert.deepEqual(
      parsed.toolCalls.map((c) => c.name),
      ["Bash", "Read"],
    );
  });

  it("flags an errored terminal result and carries its message", () => {
    const stdout = ndjson(
      { type: "system", subtype: "init", session_id: "s1" },
      {
        type: "result",
        subtype: "error_during_execution",
        is_error: true,
        error: "the model hit a fatal error",
      },
    );

    const parsed = parseStreamJson(stdout);

    assert.equal(parsed.isError, true);
    assert.equal(parsed.errorMessage, "the model hit a fatal error");
  });
});

describe("parseStreamJson — Gemini stream-json", () => {
  it("reconstructs the reply from assistant message events and reads token stats", () => {
    const stdout = ndjson(
      { type: "init", session_id: "g1", model: "gemini-3-flash-preview" },
      { type: "message", role: "user", content: "Reply with exactly: hello world\n" },
      { type: "message", role: "assistant", content: "hello world", delta: true },
      {
        type: "result",
        status: "success",
        stats: {
          total_tokens: 12855,
          input_tokens: 12687,
          output_tokens: 2,
          cached: 11087,
          models: { "gemini-3-flash-preview": { total_tokens: 12855 } },
        },
      },
    );

    const parsed = parseStreamJson(stdout);

    // Gemini's terminal result has no `result` text — the reply is the
    // accumulated assistant `message` content.
    assert.equal(parsed.reply, "hello world");
    assert.ok(parsed.cost, "Gemini token stats should yield a token-only cost");
    // Gemini reports no USD cost — costUsd is 0 (host treats as free) but
    // token usage is captured.
    assert.equal(parsed.cost!.costUsd, 0);
    assert.equal(parsed.cost!.inputTokens, 12687);
    assert.equal(parsed.cost!.outputTokens, 2);
    assert.equal(parsed.cost!.model, "gemini-3-flash-preview");
    assert.equal(parsed.isError, false);
  });

  it("concatenates multiple assistant delta events into one reply", () => {
    const stdout = ndjson(
      { type: "init", session_id: "g1" },
      { type: "message", role: "assistant", content: "hello ", delta: true },
      { type: "message", role: "assistant", content: "world", delta: true },
      { type: "result", status: "success", stats: { input_tokens: 1, output_tokens: 1 } },
    );

    const parsed = parseStreamJson(stdout);
    assert.equal(parsed.reply, "hello world");
  });

  it("flags a non-success Gemini result status as an error", () => {
    const stdout = ndjson(
      { type: "init", session_id: "g1" },
      { type: "result", status: "error", error: "quota exhausted" },
    );

    const parsed = parseStreamJson(stdout);
    assert.equal(parsed.isError, true);
    assert.equal(parsed.errorMessage, "quota exhausted");
  });
});

describe("parseStreamJson — robustness", () => {
  it("falls back to raw stdout when the output is not JSON", () => {
    const stdout = "plain assistant text, not ndjson";
    const parsed = parseStreamJson(stdout);
    assert.equal(parsed.reply, stdout);
    assert.equal(parsed.cost, null);
    assert.deepEqual(parsed.toolCalls, []);
    assert.equal(parsed.isError, false);
  });

  it("tolerates empty / whitespace stdout", () => {
    for (const stdout of ["", "   \n  "]) {
      const parsed = parseStreamJson(stdout);
      assert.equal(parsed.reply, stdout);
      assert.equal(parsed.cost, null);
    }
  });

  it("skips interleaved non-JSON log lines", () => {
    const stdout =
      "a stray log line\n" +
      JSON.stringify({ type: "message", role: "assistant", content: "ok" }) +
      "\n" +
      "another stray line\n" +
      JSON.stringify({ type: "result", status: "success", stats: { input_tokens: 1, output_tokens: 1 } }) +
      "\n";
    const parsed = parseStreamJson(stdout);
    assert.equal(parsed.reply, "ok");
    assert.equal(parsed.isError, false);
  });

  it("falls back to assistant text when there is no terminal result event", () => {
    // A stream truncated before its result event (e.g. the process was
    // killed) still surfaces whatever assistant text arrived.
    const stdout = ndjson({
      type: "assistant",
      message: { role: "assistant", content: [{ type: "text", text: "partial" }] },
    });
    const parsed = parseStreamJson(stdout);
    assert.equal(parsed.reply, "partial");
    assert.equal(parsed.cost, null);
  });
});
