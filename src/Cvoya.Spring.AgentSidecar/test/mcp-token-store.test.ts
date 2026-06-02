// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

import { strict as assert } from "node:assert";
import * as fs from "node:fs";
import * as os from "node:os";
import * as path from "node:path";
import { afterEach, beforeEach, describe, it } from "node:test";

import {
  MCP_TOKEN_FILE,
  MCP_TOKEN_PATH_ENV_VAR,
  readMcpToken,
  resolveMcpTokenPath,
  resolvePerThreadMcpTokenPath,
  writeMcpToken,
} from "../src/mcp-token-store.ts";
import { BRIDGE_STATE_DIR } from "../src/threads.ts";

let workspace: string;

beforeEach(() => {
  workspace = fs.mkdtempSync(path.join(os.tmpdir(), "sv-mcp-token-"));
});

afterEach(() => {
  fs.rmSync(workspace, { recursive: true, force: true });
});

describe("resolveMcpTokenPath", () => {
  it("returns null when no workspace path is supplied", () => {
    assert.equal(resolveMcpTokenPath(undefined), null);
    assert.equal(resolveMcpTokenPath(""), null);
  });

  it("anchors the token under <workspace>/<BRIDGE_STATE_DIR>/<MCP_TOKEN_FILE>", () => {
    const expected = path.join(workspace, BRIDGE_STATE_DIR, MCP_TOKEN_FILE);
    assert.equal(resolveMcpTokenPath(workspace), expected);
  });
});

describe("writeMcpToken / readMcpToken", () => {
  it("writes the token atomically and reads it back", () => {
    const tokenPath = resolveMcpTokenPath(workspace);
    const result = writeMcpToken(tokenPath, "turn-1-token");
    assert.equal(result.written, true);
    assert.equal(result.warning, undefined);
    assert.equal(readMcpToken(tokenPath), "turn-1-token");
  });

  it("creates the bridge state directory if it does not exist", () => {
    const tokenPath = resolveMcpTokenPath(workspace);
    assert.ok(tokenPath);
    const stateDir = path.dirname(tokenPath);
    assert.ok(!fs.existsSync(stateDir));

    writeMcpToken(tokenPath, "first-token");
    assert.ok(fs.existsSync(stateDir));
  });

  it("overwrites the file on every call (per-turn turnover)", () => {
    const tokenPath = resolveMcpTokenPath(workspace);
    writeMcpToken(tokenPath, "turn-1-token");
    writeMcpToken(tokenPath, "turn-2-token");
    assert.equal(readMcpToken(tokenPath), "turn-2-token");
  });

  it("returns empty string when the file is missing", () => {
    const tokenPath = resolveMcpTokenPath(workspace);
    // No write yet → the file does not exist.
    assert.equal(readMcpToken(tokenPath), "");
  });

  it("returns empty string when the token path is null", () => {
    assert.equal(readMcpToken(null), "");
  });

  it("no-ops when the token path is null", () => {
    const result = writeMcpToken(null, "ignored");
    assert.equal(result.written, false);
    assert.equal(result.warning, undefined);
  });

  it("writes the file with 0o600 mode (no other-readable)", () => {
    if (process.platform === "win32") {
      // POSIX-mode bits don't apply on Windows. The store still works
      // there but the mode check is a no-op.
      return;
    }
    const tokenPath = resolveMcpTokenPath(workspace);
    assert.ok(tokenPath);
    writeMcpToken(tokenPath, "secret");
    const mode = fs.statSync(tokenPath).mode & 0o777;
    assert.equal(mode, 0o600);
  });
});

describe("resolvePerThreadMcpTokenPath (#3000)", () => {
  const THREAD = "11111111-2222-3333-4444-555555555555";

  it("returns null when no workspace path is supplied", () => {
    assert.equal(resolvePerThreadMcpTokenPath(undefined, THREAD), null);
    assert.equal(resolvePerThreadMcpTokenPath("", THREAD), null);
  });

  it("returns null when no thread id is supplied (caller falls back to the shared path)", () => {
    assert.equal(resolvePerThreadMcpTokenPath(workspace, undefined), null);
    assert.equal(resolvePerThreadMcpTokenPath(workspace, ""), null);
  });

  it("returns null for a thread id that is not a safe single path segment (traversal defence)", () => {
    assert.equal(resolvePerThreadMcpTokenPath(workspace, "../evil"), null);
    assert.equal(resolvePerThreadMcpTokenPath(workspace, "a/b"), null);
    assert.equal(resolvePerThreadMcpTokenPath(workspace, "."), null);
    assert.equal(resolvePerThreadMcpTokenPath(workspace, ".."), null);
  });

  it("anchors the token under <workspace>/<BRIDGE_STATE_DIR>/threads/<threadId>/<MCP_TOKEN_FILE>", () => {
    const expected = path.join(workspace, BRIDGE_STATE_DIR, "threads", THREAD, MCP_TOKEN_FILE);
    assert.equal(resolvePerThreadMcpTokenPath(workspace, THREAD), expected);
  });

  it("gives distinct threads distinct files (no shared mutable cell under concurrency)", () => {
    const threadA = "aaaaaaaa-0000-0000-0000-000000000000";
    const threadB = "bbbbbbbb-0000-0000-0000-000000000000";
    const pathA = resolvePerThreadMcpTokenPath(workspace, threadA);
    const pathB = resolvePerThreadMcpTokenPath(workspace, threadB);
    assert.ok(pathA && pathB);
    assert.notEqual(pathA, pathB);

    // Round-trip each independently: writing one must not clobber the other,
    // which is the exact race the shared file lost under concurrent threads.
    writeMcpToken(pathA, "token-A");
    writeMcpToken(pathB, "token-B");
    assert.equal(readMcpToken(pathA), "token-A");
    assert.equal(readMcpToken(pathB), "token-B");
  });

  it("does not collide with the shared per-agent path", () => {
    assert.notEqual(resolvePerThreadMcpTokenPath(workspace, THREAD), resolveMcpTokenPath(workspace));
  });
});

describe("MCP_TOKEN_PATH_ENV_VAR", () => {
  it("is the documented per-turn token-path env var", () => {
    assert.equal(MCP_TOKEN_PATH_ENV_VAR, "SPRING_MCP_TOKEN_PATH");
  });
});
