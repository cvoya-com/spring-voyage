// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

import { strict as assert } from "node:assert";
import * as fs from "node:fs";
import * as os from "node:os";
import * as path from "node:path";
import { afterEach, beforeEach, describe, it } from "node:test";

import { BRIDGE_STATE_DIR, SEEN_THREADS_FILE, ThreadIdRegistry } from "../src/threads.ts";

let workspace: string;

beforeEach(() => {
  workspace = fs.mkdtempSync(path.join(os.tmpdir(), "sv-bridge-threads-"));
});

afterEach(() => {
  fs.rmSync(workspace, { recursive: true, force: true });
});

describe("ThreadIdRegistry", () => {
  it("starts empty when no marker file exists", () => {
    const r = new ThreadIdRegistry(workspace);
    assert.equal(r.has("00000000-0000-0000-0000-000000000001"), false);
  });

  it("records a thread id and reports it as seen on subsequent has()", () => {
    const r = new ThreadIdRegistry(workspace);
    r.add("00000000-0000-0000-0000-000000000002");
    assert.equal(r.has("00000000-0000-0000-0000-000000000002"), true);
    assert.equal(r.has("00000000-0000-0000-0000-000000000099"), false);
  });

  it("persists seen thread ids to a marker file under the workspace", () => {
    const r = new ThreadIdRegistry(workspace);
    r.add("aaaaaaaa-0000-0000-0000-000000000001");
    r.add("bbbbbbbb-0000-0000-0000-000000000002");
    const markerPath = path.join(workspace, BRIDGE_STATE_DIR, SEEN_THREADS_FILE);
    assert.ok(fs.existsSync(markerPath), `expected marker file at ${markerPath}`);
    const lines = fs.readFileSync(markerPath, "utf8").split("\n").filter((l) => l.length > 0);
    assert.deepEqual(lines.sort(), [
      "aaaaaaaa-0000-0000-0000-000000000001",
      "bbbbbbbb-0000-0000-0000-000000000002",
    ]);
  });

  it("loads seen thread ids from the marker file when constructed", () => {
    // Simulates a container restart: a previous bridge process wrote the
    // file, the new bridge boots and must remember those ids so the next
    // message uses --resume rather than --session-id.
    fs.mkdirSync(path.join(workspace, BRIDGE_STATE_DIR), { recursive: true });
    fs.writeFileSync(
      path.join(workspace, BRIDGE_STATE_DIR, SEEN_THREADS_FILE),
      "cccccccc-0000-0000-0000-000000000003\ndddddddd-0000-0000-0000-000000000004\n",
    );
    const r = new ThreadIdRegistry(workspace);
    assert.equal(r.has("cccccccc-0000-0000-0000-000000000003"), true);
    assert.equal(r.has("dddddddd-0000-0000-0000-000000000004"), true);
    assert.equal(r.has("eeeeeeee-0000-0000-0000-000000000005"), false);
  });

  it("is idempotent on add()", () => {
    const r = new ThreadIdRegistry(workspace);
    r.add("aaaaaaaa-0000-0000-0000-000000000001");
    r.add("aaaaaaaa-0000-0000-0000-000000000001");
    const markerPath = path.join(workspace, BRIDGE_STATE_DIR, SEEN_THREADS_FILE);
    const lines = fs.readFileSync(markerPath, "utf8").split("\n").filter((l) => l.length > 0);
    assert.deepEqual(lines, ["aaaaaaaa-0000-0000-0000-000000000001"]);
  });

  it("falls back to in-memory mode when no workspace path is provided", () => {
    const r = new ThreadIdRegistry(undefined);
    r.add("aaaaaaaa-0000-0000-0000-000000000001");
    assert.equal(r.has("aaaaaaaa-0000-0000-0000-000000000001"), true);
    // No marker file should be created anywhere — purely in-memory.
  });

  it("ignores corrupt marker file content (whitespace, blank lines)", () => {
    fs.mkdirSync(path.join(workspace, BRIDGE_STATE_DIR), { recursive: true });
    fs.writeFileSync(
      path.join(workspace, BRIDGE_STATE_DIR, SEEN_THREADS_FILE),
      "\n  aaaaaaaa-0000-0000-0000-000000000001  \n\n\n",
    );
    const r = new ThreadIdRegistry(workspace);
    assert.equal(r.has("aaaaaaaa-0000-0000-0000-000000000001"), true);
  });
});
