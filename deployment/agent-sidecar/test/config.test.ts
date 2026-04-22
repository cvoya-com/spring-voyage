// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

import { strict as assert } from "node:assert";
import { describe, it } from "node:test";

import { loadConfigFromEnv } from "../src/config.ts";

describe("loadConfigFromEnv", () => {
  it("uses defaults when nothing is set", () => {
    const cfg = loadConfigFromEnv({});
    assert.equal(cfg.port, 8999);
    assert.deepEqual(cfg.agentArgv, []);
    assert.equal(cfg.agentName, "Spring Voyage CLI Agent");
    assert.equal(cfg.cancelGraceMs, 5000);
  });

  it("parses SPRING_AGENT_ARGV as a JSON array", () => {
    const cfg = loadConfigFromEnv({
      SPRING_AGENT_ARGV: '["claude","--print","--input-format","stream-json"]',
    });
    assert.deepEqual(cfg.agentArgv, ["claude", "--print", "--input-format", "stream-json"]);
  });

  it("rejects SPRING_AGENT_ARGV that is not a JSON array of strings", () => {
    assert.throws(() => loadConfigFromEnv({ SPRING_AGENT_ARGV: "not-json" }), /JSON array of strings/);
    assert.throws(() => loadConfigFromEnv({ SPRING_AGENT_ARGV: "{}" }), /JSON array of strings/);
    assert.throws(() => loadConfigFromEnv({ SPRING_AGENT_ARGV: '["a", 1]' }), /JSON array of strings/);
  });

  it("rejects out-of-range AGENT_PORT", () => {
    assert.throws(() => loadConfigFromEnv({ AGENT_PORT: "0" }), /TCP port/);
    assert.throws(() => loadConfigFromEnv({ AGENT_PORT: "70000" }), /TCP port/);
    assert.throws(() => loadConfigFromEnv({ AGENT_PORT: "abc" }), /TCP port/);
  });

  it("rejects negative AGENT_CANCEL_GRACE_MS", () => {
    assert.throws(
      () => loadConfigFromEnv({ AGENT_CANCEL_GRACE_MS: "-1" }),
      /AGENT_CANCEL_GRACE_MS/,
    );
  });

  it("respects AGENT_NAME override", () => {
    const cfg = loadConfigFromEnv({ AGENT_NAME: "my-agent" });
    assert.equal(cfg.agentName, "my-agent");
  });
});
