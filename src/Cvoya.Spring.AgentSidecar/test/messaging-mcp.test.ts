// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

import { strict as assert } from "node:assert";
import * as fs from "node:fs";
import * as os from "node:os";
import * as path from "node:path";
import { afterEach, beforeEach, describe, it } from "node:test";

import { refreshMessagingToken } from "../src/messaging-mcp.ts";

let workdir: string;

beforeEach(() => {
  workdir = fs.mkdtempSync(path.join(os.tmpdir(), "sv-orch-mcp-"));
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

describe("refreshMessagingToken", () => {
  it("rewrites the spring-orchestration Authorization header with the per-message token", () => {
    const configPath = writeConfig({
      mcpServers: {
        "spring-voyage": {
          type: "http",
          url: "http://mcp",
          headers: { Authorization: "Bearer voyage-token" },
        },
        "spring-orchestration": {
          type: "http",
          url: "http://callback",
          headers: { Authorization: "Bearer launch-time-stale-token" },
        },
      },
    });

    const result = refreshMessagingToken(configPath, "fresh-per-message-token");

    assert.equal(result.refreshed, true);
    assert.equal(result.warning, undefined);
    // The spring-orchestration token is refreshed ...
    assert.equal(readAuth(configPath, "spring-orchestration"), "Bearer fresh-per-message-token");
    // ... and the unrelated spring-voyage server is left untouched.
    assert.equal(readAuth(configPath, "spring-voyage"), "Bearer voyage-token");
  });

  it("adds a headers object when the spring-orchestration block has none", () => {
    const configPath = writeConfig({
      mcpServers: {
        "spring-orchestration": { type: "http", url: "http://callback" },
      },
    });

    const result = refreshMessagingToken(configPath, "fresh-token");

    assert.equal(result.refreshed, true);
    assert.equal(readAuth(configPath, "spring-orchestration"), "Bearer fresh-token");
  });

  it("no-ops without warning when there is no spring-orchestration block", () => {
    const configPath = writeConfig({
      mcpServers: {
        "spring-voyage": { type: "http", url: "http://mcp", headers: {} },
      },
    });

    const result = refreshMessagingToken(configPath, "fresh-token");

    assert.equal(result.refreshed, false);
    assert.equal(result.warning, undefined);
    // The config is unchanged — spring-voyage still has an empty header set.
    assert.equal(readAuth(configPath, "spring-voyage"), undefined);
  });

  it("no-ops without warning when there is no mcpServers map", () => {
    const configPath = writeConfig({ something: "else" });
    const result = refreshMessagingToken(configPath, "fresh-token");
    assert.equal(result.refreshed, false);
    assert.equal(result.warning, undefined);
  });

  it("returns a warning when the config file is missing", () => {
    const result = refreshMessagingToken(
      path.join(workdir, "does-not-exist.json"),
      "fresh-token",
    );
    assert.equal(result.refreshed, false);
    assert.match(result.warning ?? "", /could not read/);
  });

  it("returns a warning when the config file is not valid JSON", () => {
    const configPath = path.join(workdir, ".mcp.json");
    fs.writeFileSync(configPath, "{ this is not json");
    const result = refreshMessagingToken(configPath, "fresh-token");
    assert.equal(result.refreshed, false);
    assert.match(result.warning ?? "", /not valid JSON/);
  });

  it("refreshes a config written with a leading UTF-8 BOM", () => {
    // .NET's static Encoding.UTF8 emits a BOM; the launcher's
    // WorkspaceMaterializer used to write `.mcp.json` that way. JSON.parse
    // rejects the BOM, so the refresh must strip it first.
    const configPath = path.join(workdir, ".mcp.json");
    const body = JSON.stringify(
      {
        mcpServers: {
          "spring-orchestration": {
            type: "http",
            url: "http://callback",
            headers: { Authorization: "Bearer launch-token" },
          },
        },
      },
      null,
      2,
    );
    fs.writeFileSync(configPath, `﻿${body}`);

    const result = refreshMessagingToken(configPath, "fresh-token");

    assert.equal(result.refreshed, true);
    assert.equal(result.warning, undefined);
    assert.equal(readAuth(configPath, "spring-orchestration"), "Bearer fresh-token");
  });

  it("is idempotent across repeated refreshes (simulating per-turn execs)", () => {
    const configPath = writeConfig({
      mcpServers: {
        "spring-orchestration": {
          type: "http",
          url: "http://callback",
          headers: { Authorization: "Bearer launch-token" },
        },
      },
    });

    refreshMessagingToken(configPath, "turn-1-token");
    assert.equal(readAuth(configPath, "spring-orchestration"), "Bearer turn-1-token");

    refreshMessagingToken(configPath, "turn-2-token");
    assert.equal(readAuth(configPath, "spring-orchestration"), "Bearer turn-2-token");
  });
});
