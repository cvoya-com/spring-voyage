// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

import { strict as assert } from "node:assert";
import { describe, it } from "node:test";

import { runAgentBridge } from "../src/bridge.ts";

const PROCESS_NODE = process.execPath;

describe("runAgentBridge", () => {
  it("captures stdout from the wrapped process", async () => {
    const result = await runAgentBridge({
      argv: [PROCESS_NODE, "-e", "process.stdout.write('hello')"],
      stdin: "",
      env: process.env,
    });
    assert.equal(result.exitCode, 0);
    assert.equal(result.stdout, "hello");
    assert.equal(result.cancelled, false);
  });

  it("forwards stdin to the child process", async () => {
    const result = await runAgentBridge({
      argv: [
        PROCESS_NODE,
        "-e",
        "let b='';process.stdin.on('data',c=>b+=c);process.stdin.on('end',()=>process.stdout.write('echo:'+b))",
      ],
      stdin: "ping",
      env: process.env,
    });
    assert.equal(result.exitCode, 0);
    assert.equal(result.stdout, "echo:ping");
  });

  it("preserves args with spaces as single argv tokens (no shell splitting)", async () => {
    // The whole 'hello world' must arrive as one argv slot. If the bridge
    // shell-splits, the child sees two args and the assertion fails.
    const result = await runAgentBridge({
      argv: [
        PROCESS_NODE,
        "-e",
        "process.stdout.write(JSON.stringify(process.argv.slice(1)))",
        "hello world",
        "--flag=value with spaces",
      ],
      stdin: "",
      env: process.env,
    });
    assert.equal(result.exitCode, 0);
    const parsed = JSON.parse(result.stdout);
    // node -e doesn't include the eval string in process.argv; only the
    // trailing user args land there. The point of the test is to confirm
    // those user args arrive as two distinct tokens with their interior
    // whitespace intact — i.e. no shell splitting on the way in.
    assert.deepEqual(parsed, ["hello world", "--flag=value with spaces"]);
  });

  it("surfaces non-zero exit codes via exitCode without throwing", async () => {
    const result = await runAgentBridge({
      argv: [PROCESS_NODE, "-e", "process.stderr.write('boom');process.exit(7)"],
      stdin: "",
      env: process.env,
    });
    assert.equal(result.exitCode, 7);
    assert.equal(result.stdout, "");
    assert.equal(result.stderr, "boom");
  });

  it("streams stderr lines through onStderrLine in order", async () => {
    const lines: string[] = [];
    const result = await runAgentBridge({
      argv: [
        PROCESS_NODE,
        "-e",
        "console.error('one');console.error('two');console.error('three')",
      ],
      stdin: "",
      env: process.env,
      onStderrLine: (l) => lines.push(l),
    });
    assert.equal(result.exitCode, 0);
    assert.deepEqual(lines, ["one", "two", "three"]);
  });

  it("rejects empty argv (dispatcher contract violation)", async () => {
    await assert.rejects(
      () =>
        runAgentBridge({
          argv: [],
          stdin: "",
          env: process.env,
        }),
      /argv is empty/,
    );
  });

  it("cancels a long-running process via abort signal", async () => {
    const ac = new AbortController();
    const start = Date.now();
    setTimeout(() => ac.abort(), 50);
    const result = await runAgentBridge({
      argv: [PROCESS_NODE, "-e", "setInterval(()=>{}, 1000)"],
      stdin: "",
      env: process.env,
      signal: ac.signal,
      cancelGraceMs: 200,
    });
    const elapsed = Date.now() - start;
    assert.equal(result.cancelled, true);
    assert.equal(result.exitCode, -1);
    // Allow generous slack for slow CI but ensure SIGTERM landed promptly.
    assert.ok(elapsed < 2000, `cancellation took too long: ${elapsed}ms`);
  });

  it("forwards a clean env (does not inherit ambient secrets)", async () => {
    process.env.UNIT_LEAK_CANARY = "leaked";
    try {
      const result = await runAgentBridge({
        argv: [PROCESS_NODE, "-e", "process.stdout.write(process.env.UNIT_LEAK_CANARY||'absent')"],
        stdin: "",
        env: { PATH: process.env.PATH ?? "" },
      });
      assert.equal(result.stdout, "absent");
    } finally {
      delete process.env.UNIT_LEAK_CANARY;
    }
  });
});
