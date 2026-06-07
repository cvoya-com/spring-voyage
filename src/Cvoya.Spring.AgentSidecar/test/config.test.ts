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

  it("defaults outputFormat to text", () => {
    assert.equal(loadConfigFromEnv({}).outputFormat, "text");
  });

  it("parses SPRING_AGENT_OUTPUT_FORMAT (case-insensitive)", () => {
    assert.equal(loadConfigFromEnv({ SPRING_AGENT_OUTPUT_FORMAT: "json" }).outputFormat, "json");
    assert.equal(loadConfigFromEnv({ SPRING_AGENT_OUTPUT_FORMAT: "JSON" }).outputFormat, "json");
    assert.equal(loadConfigFromEnv({ SPRING_AGENT_OUTPUT_FORMAT: "text" }).outputFormat, "text");
    // #2226: stream-json is the Claude/Gemini NDJSON event-stream mode.
    assert.equal(
      loadConfigFromEnv({ SPRING_AGENT_OUTPUT_FORMAT: "stream-json" }).outputFormat,
      "stream-json",
    );
    assert.equal(
      loadConfigFromEnv({ SPRING_AGENT_OUTPUT_FORMAT: "STREAM-JSON" }).outputFormat,
      "stream-json",
    );
  });

  it("rejects an unknown SPRING_AGENT_OUTPUT_FORMAT", () => {
    assert.throws(
      () => loadConfigFromEnv({ SPRING_AGENT_OUTPUT_FORMAT: "ndjson" }),
      /SPRING_AGENT_OUTPUT_FORMAT/,
    );
  });

  it("returns undefined threadBinding when neither env var is set", () => {
    const cfg = loadConfigFromEnv({});
    assert.equal(cfg.threadBinding, undefined);
  });

  it("parses SPRING_THREAD_ID_ARG_{CREATE,RESUME} into a ThreadBindingConfig", () => {
    const cfg = loadConfigFromEnv({
      SPRING_THREAD_ID_ARG_CREATE: "--session-id",
      SPRING_THREAD_ID_ARG_RESUME: "--resume",
    });
    assert.deepEqual(cfg.threadBinding, {
      createArg: "--session-id",
      resumeArg: "--resume",
    });
  });

  it("rejects partial thread-binding config (CREATE without RESUME)", () => {
    // Partial config is a launcher bug — surface loudly rather than
    // silently degrade to spawning with no session id. (ADR-0041)
    assert.throws(
      () => loadConfigFromEnv({ SPRING_THREAD_ID_ARG_CREATE: "--session-id" }),
      /set together/,
    );
  });

  it("rejects partial thread-binding config (RESUME without CREATE)", () => {
    assert.throws(
      () => loadConfigFromEnv({ SPRING_THREAD_ID_ARG_RESUME: "--resume" }),
      /set together/,
    );
  });
});
