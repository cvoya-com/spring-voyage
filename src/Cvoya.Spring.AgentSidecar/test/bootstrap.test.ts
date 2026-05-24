// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

// Unit tests for `bootstrap.ts` — the pull-based agent bootstrap client
// introduced by ADR-0055 Wave 2.

import { strict as assert } from "node:assert";
import { createHash } from "node:crypto";
import * as fs from "node:fs";
import * as os from "node:os";
import * as path from "node:path";
import { afterEach, beforeEach, describe, it } from "node:test";

import {
  BootstrapFetcher,
  BOOTSTRAP_TOKEN_ENV_VAR,
  BOOTSTRAP_URL_ENV_VAR,
  type BootstrapBundle,
  createBootstrapFetcherFromEnv,
  type FetchLike,
  resolveSafeWorkspacePath,
  WORKSPACE_PATH_ENV_VAR,
} from "../src/bootstrap.ts";

let workdir: string;

beforeEach(() => {
  workdir = fs.mkdtempSync(path.join(os.tmpdir(), "sv-bootstrap-"));
});

afterEach(() => {
  fs.rmSync(workdir, { recursive: true, force: true });
});

function sha256OfString(s: string): string {
  return "sha256:" + createHash("sha256").update(Buffer.from(s, "utf8")).digest("hex");
}

function buildBundle(
  files: { path: string; content: string }[],
  platformPaths: string[],
  version = "sha256:" + "a".repeat(64),
): BootstrapBundle {
  const fullFiles = files.map((f) => ({
    path: f.path,
    sha256: sha256OfString(f.content),
    content: f.content,
  }));
  const platformFileHashes: Record<string, string> = {};
  for (const p of platformPaths) {
    const match = fullFiles.find((f) => f.path === p);
    if (!match) throw new Error(`test bug: platform path ${p} not in files`);
    platformFileHashes[p] = match.sha256;
  }
  return {
    version,
    issuedAt: "2026-05-23T12:00:00Z",
    files: fullFiles,
    platformFileHashes,
  };
}

interface RecordedCall {
  url: string;
  ifNoneMatch: string | undefined;
  authorization: string | undefined;
}

interface FakeFetchOptions {
  responses: Array<
    | { status: 200; bundle: BootstrapBundle }
    | { status: 304 }
    | { status: number; body?: string }
  >;
}

function makeFakeFetch(opts: FakeFetchOptions): { fetchImpl: FetchLike; calls: RecordedCall[] } {
  const calls: RecordedCall[] = [];
  let i = 0;
  const fetchImpl: FetchLike = (url, init) => {
    calls.push({
      url,
      ifNoneMatch: init.headers["If-None-Match"],
      authorization: init.headers["Authorization"],
    });
    const response = opts.responses[i++];
    if (!response) {
      throw new Error(`fake fetch: no scripted response for call ${calls.length}`);
    }
    if (response.status === 304) {
      return Promise.resolve({
        status: 304,
        ok: false,
        headers: { get: () => null },
        text: () => Promise.resolve(""),
      });
    }
    if (response.status === 200 && "bundle" in response) {
      return Promise.resolve({
        status: 200,
        ok: true,
        headers: { get: () => null },
        text: () => Promise.resolve(JSON.stringify(response.bundle)),
      });
    }
    return Promise.resolve({
      status: response.status,
      ok: false,
      headers: { get: () => null },
      text: () => Promise.resolve(response.body ?? ""),
    });
  };
  return { fetchImpl, calls };
}

describe("BootstrapFetcher.fetchAndMaterialize", () => {
  it("writes every file from the bundle into the workspace", async () => {
    const bundle = buildBundle(
      [
        { path: "CLAUDE.md", content: "You are an agent." },
        { path: "connectors/example/binding.yaml", content: "id: 1\n" },
        { path: "connectors/example/state.json", content: '{"k":"v"}' },
      ],
      ["CLAUDE.md"],
    );
    const { fetchImpl, calls } = makeFakeFetch({ responses: [{ status: 200, bundle }] });

    const fetcher = new BootstrapFetcher({
      url: "http://worker/v1/bootstrap/agents/agent-1",
      token: "boot-token",
      workspacePath: workdir,
      fetchImpl,
    });

    await fetcher.fetchAndMaterialize();

    assert.equal(fs.readFileSync(path.join(workdir, "CLAUDE.md"), "utf8"), "You are an agent.");
    assert.equal(
      fs.readFileSync(path.join(workdir, "connectors/example/binding.yaml"), "utf8"),
      "id: 1\n",
    );
    assert.equal(
      fs.readFileSync(path.join(workdir, "connectors/example/state.json"), "utf8"),
      '{"k":"v"}',
    );
    assert.equal(calls.length, 1);
    assert.equal(calls[0].authorization, "Bearer boot-token");
    assert.equal(calls[0].ifNoneMatch, undefined);
  });

  it("creates intermediate directories for nested file paths", async () => {
    const bundle = buildBundle(
      [{ path: "deeply/nested/file.txt", content: "hello" }],
      [],
    );
    const { fetchImpl } = makeFakeFetch({ responses: [{ status: 200, bundle }] });
    const fetcher = new BootstrapFetcher({
      url: "http://worker/v1/bootstrap/agents/agent-1",
      token: "t",
      workspacePath: workdir,
      fetchImpl,
    });

    await fetcher.fetchAndMaterialize();

    assert.equal(fs.readFileSync(path.join(workdir, "deeply/nested/file.txt"), "utf8"), "hello");
  });

  it("caches the bundle's version so subsequent fetches can send If-None-Match", async () => {
    const bundle = buildBundle([{ path: "CLAUDE.md", content: "x" }], ["CLAUDE.md"]);
    const { fetchImpl } = makeFakeFetch({
      responses: [{ status: 200, bundle }, { status: 304 }],
    });
    const fetcher = new BootstrapFetcher({
      url: "u",
      token: "t",
      workspacePath: workdir,
      fetchImpl,
    });

    await fetcher.fetchAndMaterialize();

    assert.equal(fetcher.cachedVersion, bundle.version);
  });

  it("throws when the server returns a non-200, non-304 status", async () => {
    const { fetchImpl } = makeFakeFetch({ responses: [{ status: 401 }] });
    const fetcher = new BootstrapFetcher({
      url: "u",
      token: "wrong",
      workspacePath: workdir,
      fetchImpl,
    });

    await assert.rejects(
      () => fetcher.fetchAndMaterialize(),
      /HTTP 401/,
    );
  });

  it("throws when the response body is not valid JSON", async () => {
    const fetchImpl: FetchLike = () =>
      Promise.resolve({
        status: 200,
        ok: true,
        headers: { get: () => null },
        text: () => Promise.resolve("not-json"),
      });
    const fetcher = new BootstrapFetcher({
      url: "u",
      token: "t",
      workspacePath: workdir,
      fetchImpl,
    });

    await assert.rejects(
      () => fetcher.fetchAndMaterialize(),
      /not valid JSON/,
    );
  });

  it("throws on a malformed bundle (missing version)", async () => {
    const fetchImpl: FetchLike = () =>
      Promise.resolve({
        status: 200,
        ok: true,
        headers: { get: () => null },
        text: () =>
          Promise.resolve(
            JSON.stringify({ issuedAt: "now", files: [], platformFileHashes: {} }),
          ),
      });
    const fetcher = new BootstrapFetcher({
      url: "u",
      token: "t",
      workspacePath: workdir,
      fetchImpl,
    });

    await assert.rejects(
      () => fetcher.fetchAndMaterialize(),
      /version/,
    );
  });

  it("throws when the server unexpectedly returns 304 on the first fetch", async () => {
    const { fetchImpl } = makeFakeFetch({ responses: [{ status: 304 }] });
    const fetcher = new BootstrapFetcher({
      url: "u",
      token: "t",
      workspacePath: workdir,
      fetchImpl,
    });

    await assert.rejects(
      () => fetcher.fetchAndMaterialize(),
      /304/,
    );
  });
});

describe("BootstrapFetcher.integrityCheckAndRefresh", () => {
  async function freshFetcher(
    bundle: BootstrapBundle,
    extraResponses: FakeFetchOptions["responses"] = [],
  ): Promise<{ fetcher: BootstrapFetcher; calls: RecordedCall[] }> {
    const { fetchImpl, calls } = makeFakeFetch({
      responses: [{ status: 200, bundle }, ...extraResponses],
    });
    const fetcher = new BootstrapFetcher({
      url: "u",
      token: "t",
      workspacePath: workdir,
      fetchImpl,
    });
    await fetcher.fetchAndMaterialize();
    return { fetcher, calls };
  }

  it("issues an If-None-Match per call; a 304 with no disk divergence is a no-op", async () => {
    // Per-turn refresh: the bridge always asks the server. The
    // content-addressable etag makes 304 the common case and one HTTP
    // roundtrip is the cost of catching server-side bundle updates
    // that divergence-only checks would miss.
    const bundle = buildBundle(
      [{ path: "CLAUDE.md", content: "x" }],
      ["CLAUDE.md"],
    );
    const { fetcher, calls } = await freshFetcher(bundle, [{ status: 304 }]);

    const result = await fetcher.integrityCheckAndRefresh();

    assert.equal(result.checked, true);
    assert.deepEqual(result.restored, []);
    // Two HTTP calls: initial + the per-turn If-None-Match check.
    assert.equal(calls.length, 2);
    assert.equal(calls[1].ifNoneMatch, `"${bundle.version}"`);
  });

  it("picks up a server-side bundle update on the next turn even when nothing on disk diverged", async () => {
    // The bug this guards against: operator edits the agent's
    // Instructions between turns. The on-disk CLAUDE.md still matches
    // the cached hash (no disk drift), but the server's bundle has
    // changed. A divergence-only check would never see this. The
    // unconditional If-None-Match fetch surfaces the new bundle.
    const v1 = buildBundle(
      [{ path: "CLAUDE.md", content: "instructions v1" }],
      ["CLAUDE.md"],
      "sha256:" + "1".repeat(64),
    );
    const v2 = buildBundle(
      [{ path: "CLAUDE.md", content: "instructions v2" }],
      ["CLAUDE.md"],
      "sha256:" + "2".repeat(64),
    );
    const { fetcher, calls } = await freshFetcher(v1, [{ status: 200, bundle: v2 }]);

    // Disk is untouched — it still matches v1's cached hash.
    const result = await fetcher.integrityCheckAndRefresh();

    assert.equal(result.checked, true);
    assert.deepEqual(result.restored, ["CLAUDE.md"]);
    assert.equal(
      fs.readFileSync(path.join(workdir, "CLAUDE.md"), "utf8"),
      "instructions v2",
    );
    assert.equal(fetcher.cachedVersion, v2.version);
    // The per-turn check carried v1's etag as the If-None-Match.
    assert.equal(calls.length, 2);
    assert.equal(calls[1].ifNoneMatch, `"${v1.version}"`);
  });

  it("restores from cache on 304 when a platform file diverges on disk", async () => {
    const bundle = buildBundle(
      [
        { path: "CLAUDE.md", content: "platform-content" },
        { path: "other.txt", content: "non-platform" },
      ],
      ["CLAUDE.md"],
    );
    const { fetcher, calls } = await freshFetcher(bundle, [{ status: 304 }]);

    // Local tamper of the platform file (simulating a CLI rewrite mid-turn).
    fs.writeFileSync(path.join(workdir, "CLAUDE.md"), "tampered", "utf8");

    const result = await fetcher.integrityCheckAndRefresh();

    assert.equal(result.checked, true);
    assert.deepEqual(result.restored, ["CLAUDE.md"]);
    assert.equal(fs.readFileSync(path.join(workdir, "CLAUDE.md"), "utf8"), "platform-content");
    // Two HTTP calls: initial + the per-turn If-None-Match check.
    assert.equal(calls.length, 2);
    assert.equal(calls[1].ifNoneMatch, `"${bundle.version}"`);
  });

  it("replaces cache and re-writes every file on a 200 refresh", async () => {
    const v1 = buildBundle(
      [{ path: "CLAUDE.md", content: "v1" }],
      ["CLAUDE.md"],
      "sha256:" + "1".repeat(64),
    );
    const v2 = buildBundle(
      [
        { path: "CLAUDE.md", content: "v2" },
        { path: "connectors/example/binding.yaml", content: "id: 2\n" },
      ],
      ["CLAUDE.md"],
      "sha256:" + "2".repeat(64),
    );
    const { fetcher } = await freshFetcher(v1, [{ status: 200, bundle: v2 }]);

    // Tamper as well to exercise both the new-bundle path and the
    // implicit restoration of any pre-existing drift.
    fs.writeFileSync(path.join(workdir, "CLAUDE.md"), "tampered", "utf8");

    const result = await fetcher.integrityCheckAndRefresh();

    assert.equal(result.checked, true);
    assert.deepEqual(result.restored?.sort(), ["CLAUDE.md", "connectors/example/binding.yaml"]);
    assert.equal(fs.readFileSync(path.join(workdir, "CLAUDE.md"), "utf8"), "v2");
    assert.equal(
      fs.readFileSync(path.join(workdir, "connectors/example/binding.yaml"), "utf8"),
      "id: 2\n",
    );
    assert.equal(fetcher.cachedVersion, v2.version);
  });

  it("treats a missing platform file as divergent", async () => {
    const bundle = buildBundle(
      [{ path: "CLAUDE.md", content: "x" }],
      ["CLAUDE.md"],
    );
    const { fetcher } = await freshFetcher(bundle, [{ status: 304 }]);

    fs.rmSync(path.join(workdir, "CLAUDE.md"));

    const result = await fetcher.integrityCheckAndRefresh();

    assert.deepEqual(result.restored, ["CLAUDE.md"]);
    assert.equal(fs.readFileSync(path.join(workdir, "CLAUDE.md"), "utf8"), "x");
  });

  it("ignores divergence in non-platform files (server returns 304)", async () => {
    // .mcp.json (or any non-pinned file) the sidecar should NOT police —
    // it lives in `files` but not in `platformFileHashes`. The per-turn
    // refresh still runs (one HTTP call), but neither the divergent
    // file nor the cache is touched.
    const bundle = buildBundle(
      [
        { path: "CLAUDE.md", content: "platform" },
        { path: ".mcp.json", content: '{"servers":{}}' },
      ],
      ["CLAUDE.md"],
    );
    const { fetcher, calls } = await freshFetcher(bundle, [{ status: 304 }]);

    fs.writeFileSync(path.join(workdir, ".mcp.json"), '{"tampered":true}', "utf8");

    const result = await fetcher.integrityCheckAndRefresh();

    assert.equal(result.checked, true);
    assert.deepEqual(result.restored, []);
    // Two HTTP calls: initial + per-turn check.
    assert.equal(calls.length, 2);
    // The tampered non-platform file is left as-is.
    assert.equal(
      fs.readFileSync(path.join(workdir, ".mcp.json"), "utf8"),
      '{"tampered":true}',
    );
  });

  it("falls back to disk-only restoration with a warning when the fetch fails", async () => {
    const bundle = buildBundle(
      [{ path: "CLAUDE.md", content: "x" }],
      ["CLAUDE.md"],
    );
    const { fetcher } = await freshFetcher(bundle, [{ status: 502 }]);
    fs.writeFileSync(path.join(workdir, "CLAUDE.md"), "tampered", "utf8");

    const result = await fetcher.integrityCheckAndRefresh();

    assert.equal(result.checked, true);
    assert.ok(result.warning?.includes("bootstrap fetch failed"));
    // Disk drift still restored from the cached bytes — the warning
    // surfaces the network problem but the turn is not derailed.
    assert.deepEqual(result.restored, ["CLAUDE.md"]);
    assert.equal(fs.readFileSync(path.join(workdir, "CLAUDE.md"), "utf8"), "x");
  });
});

describe("createBootstrapFetcherFromEnv", () => {
  it("returns null when SPRING_BOOTSTRAP_URL is unset", () => {
    const fetcher = createBootstrapFetcherFromEnv({});
    assert.equal(fetcher, null);
  });

  it("throws when URL is set but TOKEN is not", () => {
    assert.throws(
      () =>
        createBootstrapFetcherFromEnv({
          [BOOTSTRAP_URL_ENV_VAR]: "http://worker/v1/bootstrap/agents/a",
          [WORKSPACE_PATH_ENV_VAR]: "/spring/members/a",
        }),
      /SPRING_BOOTSTRAP_TOKEN/,
    );
  });

  it("throws when URL is set but WORKSPACE_PATH is not", () => {
    assert.throws(
      () =>
        createBootstrapFetcherFromEnv({
          [BOOTSTRAP_URL_ENV_VAR]: "http://worker/v1/bootstrap/agents/a",
          [BOOTSTRAP_TOKEN_ENV_VAR]: "tok",
        }),
      /SPRING_WORKSPACE_PATH/,
    );
  });

  it("returns a fetcher when all three env vars are set", () => {
    const fetcher = createBootstrapFetcherFromEnv({
      [BOOTSTRAP_URL_ENV_VAR]: "http://worker/v1/bootstrap/agents/a",
      [BOOTSTRAP_TOKEN_ENV_VAR]: "tok",
      [WORKSPACE_PATH_ENV_VAR]: "/spring/members/a",
    });
    assert.ok(fetcher !== null);
  });
});

describe("resolveSafeWorkspacePath", () => {
  it("accepts a simple relative path", () => {
    const resolved = resolveSafeWorkspacePath(workdir, "CLAUDE.md");
    assert.equal(resolved, path.resolve(workdir, "CLAUDE.md"));
  });

  it("accepts a nested relative path", () => {
    const resolved = resolveSafeWorkspacePath(workdir, "connectors/example/binding.yaml");
    assert.equal(resolved, path.resolve(workdir, "connectors/example/binding.yaml"));
  });

  it("rejects an absolute path", () => {
    assert.throws(
      () => resolveSafeWorkspacePath(workdir, "/etc/passwd"),
      /must be relative/,
    );
  });

  it("rejects a parent-traversal escape", () => {
    assert.throws(
      () => resolveSafeWorkspacePath(workdir, "../../../etc/passwd"),
      /escapes the workspace root/,
    );
  });

  it("rejects an empty path", () => {
    assert.throws(() => resolveSafeWorkspacePath(workdir, ""), /must not be empty/);
  });

  it("rejects a Windows-style absolute path", () => {
    assert.throws(
      () => resolveSafeWorkspacePath(workdir, "C:\\Windows\\system32"),
      /must be relative/,
    );
  });
});
