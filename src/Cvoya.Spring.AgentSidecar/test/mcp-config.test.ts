// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

import { strict as assert } from "node:assert";
import * as fs from "node:fs";
import * as os from "node:os";
import * as path from "node:path";
import { afterEach, beforeEach, describe, it } from "node:test";

import { rewriteMcpToken } from "../src/mcp-config.ts";

let workdir: string;

beforeEach(() => {
  workdir = fs.mkdtempSync(path.join(os.tmpdir(), "sv-mcp-config-"));
});

afterEach(() => {
  fs.rmSync(workdir, { recursive: true, force: true });
});

function writeConfig(contents: unknown): string {
  const configPath = path.join(workdir, ".mcp.json");
  fs.writeFileSync(configPath, JSON.stringify(contents, null, 2));
  return configPath;
}

function readAuth(configPath: string, server: string): string | undefined {
  const parsed = JSON.parse(fs.readFileSync(configPath, "utf8"));
  return parsed?.mcpServers?.[server]?.headers?.Authorization;
}

describe("rewriteMcpToken", () => {
  it("rewrites the spring-voyage Authorization header with the per-turn token", () => {
    const configPath = writeConfig({
      mcpServers: {
        "spring-voyage": {
          type: "http",
          url: "http://mcp",
          headers: { Authorization: "Bearer launch-time-empty-token" },
        },
      },
    });

    const result = rewriteMcpToken(configPath, "fresh-per-turn-token");

    assert.equal(result.rewritten, true);
    assert.equal(result.warning, undefined);
    assert.equal(readAuth(configPath, "spring-voyage"), "Bearer fresh-per-turn-token");
  });

  it("leaves unrelated server blocks untouched", () => {
    const configPath = writeConfig({
      mcpServers: {
        "spring-voyage": {
          type: "http",
          url: "http://mcp",
          headers: { Authorization: "Bearer launch-token" },
        },
        "some-other-server": {
          type: "http",
          url: "http://other",
          headers: { Authorization: "Bearer other-token" },
        },
      },
    });

    const result = rewriteMcpToken(configPath, "fresh-per-turn-token");

    assert.equal(result.rewritten, true);
    assert.equal(readAuth(configPath, "spring-voyage"), "Bearer fresh-per-turn-token");
    assert.equal(readAuth(configPath, "some-other-server"), "Bearer other-token");
  });

  it("adds a headers object when the spring-voyage block has none", () => {
    const configPath = writeConfig({
      mcpServers: {
        "spring-voyage": { type: "http", url: "http://mcp" },
      },
    });

    const result = rewriteMcpToken(configPath, "fresh-token");

    assert.equal(result.rewritten, true);
    assert.equal(readAuth(configPath, "spring-voyage"), "Bearer fresh-token");
  });

  it("rewrites a Gemini-shaped settings file (mcpServers.<name>.headers)", () => {
    // Gemini's .gemini/settings.json carries the same
    // mcpServers.<name>.headers.Authorization shape as .mcp.json.
    const configPath = path.join(workdir, "settings.json");
    fs.writeFileSync(
      configPath,
      JSON.stringify({
        mcpServers: {
          "spring-voyage": {
            httpUrl: "http://mcp",
            headers: { Authorization: "Bearer launch-token" },
          },
        },
      }),
    );

    const result = rewriteMcpToken(configPath, "fresh-token");

    assert.equal(result.rewritten, true);
    assert.equal(readAuth(configPath, "spring-voyage"), "Bearer fresh-token");
  });

  it("no-ops without warning when there is no spring-voyage block", () => {
    const configPath = writeConfig({
      mcpServers: {
        "some-other-server": { type: "http", url: "http://other", headers: {} },
      },
    });

    const result = rewriteMcpToken(configPath, "fresh-token");

    assert.equal(result.rewritten, false);
    assert.equal(result.warning, undefined);
  });

  it("no-ops without warning when there is no mcpServers map", () => {
    const configPath = writeConfig({ something: "else" });
    const result = rewriteMcpToken(configPath, "fresh-token");
    assert.equal(result.rewritten, false);
    assert.equal(result.warning, undefined);
  });

  it("returns a warning when the config file is missing", () => {
    const result = rewriteMcpToken(
      path.join(workdir, "does-not-exist.json"),
      "fresh-token",
    );
    assert.equal(result.rewritten, false);
    assert.match(result.warning ?? "", /could not read/);
  });

  it("returns a warning when the config file is not valid JSON", () => {
    const configPath = path.join(workdir, ".mcp.json");
    fs.writeFileSync(configPath, "{ this is not json");
    const result = rewriteMcpToken(configPath, "fresh-token");
    assert.equal(result.rewritten, false);
    assert.match(result.warning ?? "", /not valid JSON/);
  });

  it("rewrites a config written with a leading UTF-8 BOM", () => {
    // .NET's static Encoding.UTF8 emits a BOM; JSON.parse rejects the BOM,
    // so the rewrite must strip it first.
    const configPath = path.join(workdir, ".mcp.json");
    const body = JSON.stringify(
      {
        mcpServers: {
          "spring-voyage": {
            type: "http",
            url: "http://mcp",
            headers: { Authorization: "Bearer launch-token" },
          },
        },
      },
      null,
      2,
    );
    fs.writeFileSync(configPath, `﻿${body}`);

    const result = rewriteMcpToken(configPath, "fresh-token");

    assert.equal(result.rewritten, true);
    assert.equal(result.warning, undefined);
    assert.equal(readAuth(configPath, "spring-voyage"), "Bearer fresh-token");
  });

  it("is idempotent across repeated rewrites (simulating per-turn execs)", () => {
    const configPath = writeConfig({
      mcpServers: {
        "spring-voyage": {
          type: "http",
          url: "http://mcp",
          headers: { Authorization: "Bearer launch-token" },
        },
      },
    });

    rewriteMcpToken(configPath, "turn-1-token");
    assert.equal(readAuth(configPath, "spring-voyage"), "Bearer turn-1-token");

    rewriteMcpToken(configPath, "turn-2-token");
    assert.equal(readAuth(configPath, "spring-voyage"), "Bearer turn-2-token");
  });
});
