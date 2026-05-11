// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

// The bridge wraps a stdin/stdout-driven CLI behind an A2A `message/send`
// invocation. It is intentionally agent-agnostic: we spawn whatever argv
// vector the dispatcher handed us, pipe the user prompt to stdin, collect
// stdout, and surface stderr lines as task status updates.
//
// PR 3a of #1087.

import { spawn, type ChildProcess } from "node:child_process";

export interface BridgeRunOptions {
  // The argv vector to spawn. argv[0] is the executable. Required: an
  // empty argv means the dispatcher mis-configured the launcher.
  argv: string[];
  // The prompt body to feed via stdin. Bridge closes stdin after writing.
  stdin: string;
  // Environment variables for the child process. The bridge does not
  // inherit its own env by default — only the values the launcher set.
  env: NodeJS.ProcessEnv;
  // Working directory for the child. Defaults to process.cwd().
  cwd?: string;
  // Cancellation signal. Aborting issues SIGTERM, then SIGKILL after the
  // grace period if the child has not exited.
  signal?: AbortSignal;
  // Grace period (ms) between SIGTERM and SIGKILL on cancellation.
  cancelGraceMs?: number;
  // Optional callback invoked for every stderr line captured. Lets the
  // server stream stderr as A2A `TaskStatusUpdate` artifacts without the
  // bridge needing to know about A2A semantics.
  onStderrLine?: (line: string) => void;
}

export interface BridgeRunResult {
  // Process exit code. -1 if the child was cancelled by signal.
  exitCode: number;
  // Captured stdout, decoded as UTF-8 with replacement.
  stdout: string;
  // Captured stderr (full text). Streaming consumers should rely on
  // onStderrLine; this is here for the terminal artifact path.
  stderr: string;
  // True iff the bridge cancelled the process (SIGTERM/SIGKILL) in
  // response to options.signal aborting.
  cancelled: boolean;
}

const DEFAULT_GRACE_MS = 5000;

export async function runAgentBridge(opts: BridgeRunOptions): Promise<BridgeRunResult> {
  if (opts.argv.length === 0) {
    throw new Error("bridge.runAgentBridge: argv is empty (dispatcher must set SPRING_AGENT_ARGV)");
  }

  const [command, ...args] = opts.argv as [string, ...string[]];

  // Spawn with a clean env — only the launcher-provided values flow in.
  // shell:false guarantees no shell expansion. Each arg is forwarded as
  // a distinct token (matches the IReadOnlyList<string> contract on the
  // dispatcher side from PR 1).
  const child: ChildProcess = spawn(command, args, {
    cwd: opts.cwd ?? process.cwd(),
    env: opts.env,
    stdio: ["pipe", "pipe", "pipe"],
    shell: false,
  });

  let cancelled = false;
  let killTimer: NodeJS.Timeout | undefined;

  const onAbort = () => {
    if (child.exitCode !== null || cancelled) {
      return;
    }
    cancelled = true;
    try {
      child.kill("SIGTERM");
    } catch {
      // process is already gone
    }
    const grace = opts.cancelGraceMs ?? DEFAULT_GRACE_MS;
    killTimer = setTimeout(() => {
      try {
        child.kill("SIGKILL");
      } catch {
        // already gone
      }
    }, grace);
    killTimer.unref();
  };

  if (opts.signal) {
    if (opts.signal.aborted) {
      onAbort();
    } else {
      opts.signal.addEventListener("abort", onAbort, { once: true });
    }
  }

  // Feed stdin. The CLI is expected to close on EOF.
  if (child.stdin) {
    child.stdin.end(opts.stdin);
  }

  const stdoutChunks: Buffer[] = [];
  const stderrChunks: Buffer[] = [];

  if (child.stdout) {
    child.stdout.on("data", (chunk: Buffer) => {
      stdoutChunks.push(chunk);
    });
  }

  if (child.stderr) {
    let stderrBuffer = "";
    child.stderr.on("data", (chunk: Buffer) => {
      stderrChunks.push(chunk);
      if (!opts.onStderrLine) {
        return;
      }
      stderrBuffer += chunk.toString("utf8");
      // Emit complete lines only; partial trailing line stays in the buffer.
      let newlineIdx = stderrBuffer.indexOf("\n");
      while (newlineIdx !== -1) {
        const line = stderrBuffer.slice(0, newlineIdx);
        stderrBuffer = stderrBuffer.slice(newlineIdx + 1);
        try {
          opts.onStderrLine(line);
        } catch {
          // Listener errors are not fatal to the bridge.
        }
        newlineIdx = stderrBuffer.indexOf("\n");
      }
    });
    child.stderr.on("end", () => {
      if (stderrBuffer.length > 0 && opts.onStderrLine) {
        try {
          opts.onStderrLine(stderrBuffer);
        } catch {
          // ignore
        }
        stderrBuffer = "";
      }
    });
  }

  const exitCode = await new Promise<number>((resolve, reject) => {
    child.once("error", (err) => {
      // ENOENT and friends: surface as failure with a meaningful message
      // rather than a process-wide crash.
      reject(err);
    });
    child.once("close", (code, _signal) => {
      resolve(typeof code === "number" ? code : -1);
    });
  }).finally(() => {
    if (killTimer) {
      clearTimeout(killTimer);
    }
    if (opts.signal) {
      opts.signal.removeEventListener("abort", onAbort);
    }
  });

  return {
    exitCode: cancelled ? -1 : exitCode,
    stdout: Buffer.concat(stdoutChunks).toString("utf8"),
    stderr: Buffer.concat(stderrChunks).toString("utf8"),
    cancelled,
  };
}
