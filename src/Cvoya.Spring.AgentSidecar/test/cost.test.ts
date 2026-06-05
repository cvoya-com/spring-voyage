// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

import { strict as assert } from "node:assert";
import { describe, it } from "node:test";

import { parseCliJsonResult } from "../src/cost.ts";

describe("parseCliJsonResult", () => {
  it("extracts reply text and cost from a Claude Code json result", () => {
    const stdout = JSON.stringify({
      type: "result",
      subtype: "success",
      is_error: false,
      result: "Here is the answer.",
      total_cost_usd: 0.0123,
      usage: {
        input_tokens: 100,
        cache_creation_input_tokens: 10,
        cache_read_input_tokens: 5,
        output_tokens: 50,
      },
      modelUsage: { "claude-opus-4-8": { costUSD: 0.0123 } },
    });

    const parsed = parseCliJsonResult(stdout);

    assert.equal(parsed.reply, "Here is the answer.");
    assert.ok(parsed.cost);
    assert.equal(parsed.cost!.costUsd, 0.0123);
    // input tokens include cache-creation + cache-read tokens.
    assert.equal(parsed.cost!.inputTokens, 115);
    assert.equal(parsed.cost!.outputTokens, 50);
    assert.equal(parsed.cost!.model, "claude-opus-4-8");
  });

  it("returns no cost when total_cost_usd is absent", () => {
    const stdout = JSON.stringify({
      result: "free turn",
      usage: { input_tokens: 10, output_tokens: 5 },
    });
    const parsed = parseCliJsonResult(stdout);
    assert.equal(parsed.reply, "free turn");
    assert.equal(parsed.cost, null);
  });

  it("returns no cost when total_cost_usd is zero or negative", () => {
    for (const cost of [0, -1]) {
      const parsed = parseCliJsonResult(
        JSON.stringify({ result: "x", total_cost_usd: cost }),
      );
      assert.equal(parsed.cost, null, `cost ${cost} should yield null`);
      assert.equal(parsed.reply, "x");
    }
  });

  it("falls back to raw stdout when the output is not JSON", () => {
    const stdout = "plain assistant text, not json";
    const parsed = parseCliJsonResult(stdout);
    assert.equal(parsed.reply, stdout);
    assert.equal(parsed.cost, null);
  });

  it("falls back to raw stdout when the result field is missing", () => {
    const stdout = JSON.stringify({ total_cost_usd: 0.01, usage: {} });
    const parsed = parseCliJsonResult(stdout);
    // No `.result` string — surface the raw stdout rather than dropping output.
    assert.equal(parsed.reply, stdout);
    // ...but a positive cost is still captured.
    assert.ok(parsed.cost);
    assert.equal(parsed.cost!.costUsd, 0.01);
    assert.equal(parsed.cost!.inputTokens, 0);
    assert.equal(parsed.cost!.outputTokens, 0);
    assert.equal(parsed.cost!.model, null);
  });

  it("uses a top-level model field when modelUsage is absent", () => {
    const parsed = parseCliJsonResult(
      JSON.stringify({ result: "x", total_cost_usd: 0.02, model: "claude-sonnet-4-6" }),
    );
    assert.equal(parsed.cost!.model, "claude-sonnet-4-6");
  });

  it("tolerates empty / whitespace stdout", () => {
    for (const stdout of ["", "   \n  "]) {
      const parsed = parseCliJsonResult(stdout);
      assert.equal(parsed.reply, stdout);
      assert.equal(parsed.cost, null);
    }
  });

  it("ignores a JSON array (only single result objects carry cost)", () => {
    const stdout = JSON.stringify([{ total_cost_usd: 1 }]);
    const parsed = parseCliJsonResult(stdout);
    assert.equal(parsed.reply, stdout);
    assert.equal(parsed.cost, null);
  });
});
