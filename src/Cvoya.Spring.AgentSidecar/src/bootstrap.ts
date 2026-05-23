// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

// Pull-based agent bootstrap client (ADR-0055). On container start the
// sidecar pulls its workspace files from the worker-hosted endpoint
// `GET $SPRING_BOOTSTRAP_URL` with `Authorization: Bearer
// $SPRING_BOOTSTRAP_TOKEN`. Before every CLI spawn it re-checks the
// platform-authoritative files (ADR-0055 §6) — any divergence triggers
// a re-pull (If-None-Match on the cached etag). A 304 means the server
// confirms the cache is fresh and the local divergence is restored
// from the cached bytes; a 200 replaces the cache and re-writes
// everything.
//
// The fetcher is dormant in Wave 2: the env vars are not stamped on
// container launch yet (Wave 3 wires the launchers). When the URL is
// unset, `createBootstrapFetcherFromEnv` returns `null` and the
// sidecar runs exactly as it does today.

import { createHash } from "node:crypto";
import * as fs from "node:fs";
import * as path from "node:path";

/**
 * Env var name carrying the absolute URL of the worker bootstrap
 * endpoint the sidecar pulls its configuration from (ADR-0055 §9).
 */
export const BOOTSTRAP_URL_ENV_VAR = "SPRING_BOOTSTRAP_URL";

/**
 * Env var name carrying the per-agent bootstrap bearer token (ADR-0055 §8).
 */
export const BOOTSTRAP_TOKEN_ENV_VAR = "SPRING_BOOTSTRAP_TOKEN";

/**
 * Env var name carrying the in-container path of the per-agent workspace
 * mount where bundle files are materialised (D1 § 2.2.1, ADR-0029).
 */
export const WORKSPACE_PATH_ENV_VAR = "SPRING_WORKSPACE_PATH";

/**
 * Wire shape of one file in a bootstrap bundle. Mirrors the C# DTO
 * `Cvoya.Spring.Core.Execution.AgentBootstrapFile`.
 */
export interface BootstrapFile {
  path: string;
  sha256: string;
  content: string;
}

/**
 * Wire shape of the bootstrap bundle. Mirrors the C# DTO
 * `Cvoya.Spring.Core.Execution.AgentBootstrapBundle`.
 */
export interface BootstrapBundle {
  version: string;
  issuedAt: string;
  files: BootstrapFile[];
  platformFileHashes: Record<string, string>;
}

/** Subset of `fetch` the fetcher actually uses. Tests inject a stub. */
export type FetchLike = (
  url: string,
  init: { method: string; headers: Record<string, string> },
) => Promise<{
  status: number;
  ok: boolean;
  headers: { get: (name: string) => string | null };
  text: () => Promise<string>;
}>;

export interface BootstrapFetcherDeps {
  /** Absolute URL of the worker bootstrap endpoint. */
  url: string;
  /** Opaque per-agent bearer (ADR-0055 §8). */
  token: string;
  /** Workspace root inside the container — bundle paths are relative to here. */
  workspacePath: string;
  /** Override the HTTP client. Defaults to the global `fetch`. */
  fetchImpl?: FetchLike;
}

interface CachedBundle {
  /** ETag string as the server returned it — already quoted, e.g. `"sha256:..."`. */
  etag: string;
  /** All file contents the server emitted, keyed by workspace-relative path. */
  files: Map<string, string>;
  /** sha256 hashes for the platform-authoritative subset, keyed by path. */
  platformFileHashes: Map<string, string>;
}

/**
 * Pulls the agent's bootstrap bundle and keeps the on-disk workspace in
 * sync with the platform-authoritative subset.
 */
export class BootstrapFetcher {
  private readonly url: string;
  private readonly token: string;
  private readonly workspacePath: string;
  private readonly fetchImpl: FetchLike;
  private cache: CachedBundle | null = null;

  constructor(deps: BootstrapFetcherDeps) {
    if (!deps.url || deps.url.length === 0) {
      throw new Error("BootstrapFetcher requires a non-empty url.");
    }
    if (!deps.token || deps.token.length === 0) {
      throw new Error("BootstrapFetcher requires a non-empty token.");
    }
    if (!deps.workspacePath || deps.workspacePath.length === 0) {
      throw new Error("BootstrapFetcher requires a non-empty workspacePath.");
    }
    this.url = deps.url;
    this.token = deps.token;
    this.workspacePath = deps.workspacePath;
    // Cast: the global `fetch` is wider than `FetchLike`, but `FetchLike`
    // is a strict subset of the calls the fetcher makes — runtime
    // behaviour is identical.
    this.fetchImpl =
      deps.fetchImpl ?? (globalThis.fetch as unknown as FetchLike);
  }

  /**
   * Pulls the bundle and materialises every file onto the workspace
   * volume. Must complete before the sidecar accepts HTTP traffic
   * (cli.ts blocks server.listen() on this). Throws on any failure —
   * the container should fail loudly rather than start with a
   * half-populated workspace.
   */
  async fetchAndMaterialize(): Promise<void> {
    const bundle = await this.doFetch(undefined);
    if (bundle === null) {
      // The server would only return 304 if we presented an If-None-Match,
      // and we did not. Defensive: a misbehaving proxy could still inject
      // one. Treat as a contract violation.
      throw new Error(
        "Bootstrap server returned 304 on first fetch — no If-None-Match was sent.",
      );
    }
    this.materializeAll(bundle);
    this.cache = this.toCached(bundle, this.buildEtag(bundle.version));
  }

  /**
   * Runs the per-turn refresh (ADR-0055 §6). Always issues an
   * `If-None-Match` fetch — the worker's content-addressable etag
   * makes the 304 path one HTTP roundtrip with an empty body, while
   * the 200 path delivers server-side updates that disk-divergence
   * alone would never surface (e.g. the operator edited the agent's
   * `Instructions` between turns; the on-disk `CLAUDE.md` still
   * matches the cached hash so a divergence-only check would silently
   * keep using the stale bundle).
   *
   * Result handling:
   * - 304 — server confirms our cached bundle is current. Restore any
   *   platform file that drifted on disk (e.g. the CLI rewrote
   *   `CLAUDE.md` mid-turn) from the cached bytes.
   * - 200 — server returned a fresh bundle. Replace the cache and
   *   re-write every file.
   * - fetch failure — best-effort: fall back to disk-only restoration
   *   from the existing cache so a transient network blip doesn't
   *   take down the turn.
   */
  async integrityCheckAndRefresh(): Promise<IntegrityCheckResult> {
    if (this.cache === null) {
      // Defensive: integrity-check called before the initial fetch. The
      // sidecar's startup path guarantees fetchAndMaterialize() runs first,
      // but a future caller (test harness, alternative entry point) could
      // call this directly. No-op rather than throwing.
      return { checked: false, warning: "no bundle cached; skipping integrity check" };
    }

    let refreshed: BootstrapBundle | null;
    try {
      refreshed = await this.doFetch(this.cache.etag);
    } catch (err) {
      // Fetch failed — fall back to disk-only divergence restoration
      // from the existing cache. Surface as a warning.
      const restored = this.restoreDivergedFromCache();
      return {
        checked: true,
        restored,
        warning: `bootstrap fetch failed (${(err as Error).message}); restored ${restored.length} diverged file(s) from cache`,
      };
    }

    if (refreshed === null) {
      // 304 — bundle unchanged; restore any on-disk drift from cache.
      const restored = this.restoreDivergedFromCache();
      return { checked: true, restored };
    }

    // 200 — server returned a new bundle. This is the per-turn
    // refresh path: an operator edited the agent definition, a
    // connector contribution changed, etc. Replace the cache and
    // re-write every file so the CLI spawn that follows sees the
    // fresh content.
    this.materializeAll(refreshed);
    this.cache = this.toCached(refreshed, this.buildEtag(refreshed.version));
    return { checked: true, restored: Array.from(this.cache.files.keys()) };
  }

  /**
   * Recomputes the platform-authoritative subset against disk and
   * restores any diverged file from the in-memory cache. No HTTP
   * calls. Returns the list of paths actually restored — empty when
   * the disk already matches the cache.
   */
  private restoreDivergedFromCache(): string[] {
    if (this.cache === null) {
      return [];
    }
    let diverged: string[];
    try {
      diverged = this.findDivergedPlatformFiles();
    } catch {
      // findDivergedPlatformFiles only throws on workspace read
      // failure; treat as "nothing to restore" rather than masking a
      // 304 result.
      return [];
    }
    const restored: string[] = [];
    for (const filePath of diverged) {
      const cached = this.cache.files.get(filePath);
      if (cached !== undefined) {
        this.materializeOne(filePath, cached);
        restored.push(filePath);
      }
    }
    return restored;
  }

  /**
   * Returns the cached bundle's version string (`sha256:<hex>`), or
   * `null` when no bundle has been fetched yet. Exposed for tests and
   * for debug-level logging.
   */
  get cachedVersion(): string | null {
    return this.cache?.etag.replace(/^"|"$/g, "") ?? null;
  }

  private async doFetch(ifNoneMatch: string | undefined): Promise<BootstrapBundle | null> {
    const headers: Record<string, string> = {
      Authorization: `Bearer ${this.token}`,
      Accept: "application/json",
    };
    if (ifNoneMatch) {
      headers["If-None-Match"] = ifNoneMatch;
    }

    const response = await this.fetchImpl(this.url, { method: "GET", headers });

    if (response.status === 304) {
      return null;
    }
    if (!response.ok) {
      throw new Error(`bootstrap fetch ${this.url} returned HTTP ${response.status}`);
    }

    const body = await response.text();
    let parsed: unknown;
    try {
      parsed = JSON.parse(body);
    } catch (err) {
      throw new Error(`bootstrap response is not valid JSON: ${(err as Error).message}`);
    }

    return validateBundle(parsed);
  }

  private materializeAll(bundle: BootstrapBundle): void {
    for (const file of bundle.files) {
      this.materializeOne(file.path, file.content);
    }
  }

  private materializeOne(relativePath: string, content: string): void {
    const absolute = resolveSafeWorkspacePath(this.workspacePath, relativePath);
    fs.mkdirSync(path.dirname(absolute), { recursive: true });
    fs.writeFileSync(absolute, content, "utf8");
  }

  private findDivergedPlatformFiles(): string[] {
    if (this.cache === null) {
      return [];
    }
    const diverged: string[] = [];
    for (const [filePath, expectedHash] of this.cache.platformFileHashes) {
      const absolute = resolveSafeWorkspacePath(this.workspacePath, filePath);
      let actual: string;
      if (!fs.existsSync(absolute)) {
        diverged.push(filePath);
        continue;
      }
      const buf = fs.readFileSync(absolute);
      actual = "sha256:" + createHash("sha256").update(buf).digest("hex");
      if (actual !== expectedHash) {
        diverged.push(filePath);
      }
    }
    return diverged;
  }

  private toCached(bundle: BootstrapBundle, etag: string): CachedBundle {
    return {
      etag,
      files: new Map(bundle.files.map((f) => [f.path, f.content])),
      platformFileHashes: new Map(Object.entries(bundle.platformFileHashes)),
    };
  }

  private buildEtag(version: string): string {
    return `"${version}"`;
  }
}

/**
 * Result of one integrity-check pass. Used by the caller (a2a.ts) for
 * structured logging — failures do not throw.
 */
export interface IntegrityCheckResult {
  /** True iff the on-disk hashes were inspected. */
  checked: boolean;
  /** Workspace-relative paths the fetcher rewrote during this pass. */
  restored?: string[];
  /** Set when the check or refresh could not complete. */
  warning?: string;
}

/**
 * Constructs a `BootstrapFetcher` from `process.env`. Returns `null`
 * when `SPRING_BOOTSTRAP_URL` is unset — the sidecar starts with the
 * pre-pull behaviour (Wave 3 turns this on by stamping the env vars
 * at launch).
 */
export function createBootstrapFetcherFromEnv(
  env: NodeJS.ProcessEnv,
  fetchImpl?: FetchLike,
): BootstrapFetcher | null {
  const url = env[BOOTSTRAP_URL_ENV_VAR];
  if (!url || url.length === 0) {
    return null;
  }
  const token = env[BOOTSTRAP_TOKEN_ENV_VAR];
  if (!token || token.length === 0) {
    throw new Error(
      `${BOOTSTRAP_URL_ENV_VAR} is set but ${BOOTSTRAP_TOKEN_ENV_VAR} is empty. ` +
        "The launcher must stamp both together (ADR-0055 §9).",
    );
  }
  const workspacePath = env[WORKSPACE_PATH_ENV_VAR];
  if (!workspacePath || workspacePath.length === 0) {
    throw new Error(
      `${BOOTSTRAP_URL_ENV_VAR} is set but ${WORKSPACE_PATH_ENV_VAR} is empty. ` +
        "The launcher must stamp the workspace mount path before the sidecar can materialise files.",
    );
  }
  return new BootstrapFetcher({ url, token, workspacePath, fetchImpl });
}

function validateBundle(value: unknown): BootstrapBundle {
  if (!value || typeof value !== "object") {
    throw new Error("bootstrap response is not a JSON object");
  }
  const obj = value as Record<string, unknown>;
  const version = obj["version"];
  const issuedAt = obj["issuedAt"];
  const files = obj["files"];
  const platformFileHashes = obj["platformFileHashes"];

  if (typeof version !== "string" || !version.startsWith("sha256:")) {
    throw new Error("bootstrap response: missing or malformed `version` (expected `sha256:<hex>`)");
  }
  if (typeof issuedAt !== "string") {
    throw new Error("bootstrap response: missing or malformed `issuedAt`");
  }
  if (!Array.isArray(files)) {
    throw new Error("bootstrap response: `files` must be an array");
  }
  if (!platformFileHashes || typeof platformFileHashes !== "object") {
    throw new Error("bootstrap response: `platformFileHashes` must be an object");
  }

  const validatedFiles: BootstrapFile[] = [];
  for (const entry of files) {
    if (!entry || typeof entry !== "object") {
      throw new Error("bootstrap response: each file must be an object");
    }
    const e = entry as Record<string, unknown>;
    if (typeof e["path"] !== "string" || e["path"].length === 0) {
      throw new Error("bootstrap response: file.path must be a non-empty string");
    }
    if (typeof e["sha256"] !== "string") {
      throw new Error("bootstrap response: file.sha256 must be a string");
    }
    if (typeof e["content"] !== "string") {
      throw new Error("bootstrap response: file.content must be a string");
    }
    validatedFiles.push({
      path: e["path"],
      sha256: e["sha256"],
      content: e["content"],
    });
  }

  const validatedHashes: Record<string, string> = {};
  for (const [k, v] of Object.entries(platformFileHashes)) {
    if (typeof v !== "string") {
      throw new Error(`bootstrap response: platformFileHashes[${k}] must be a string`);
    }
    validatedHashes[k] = v;
  }

  return { version, issuedAt, files: validatedFiles, platformFileHashes: validatedHashes };
}

/**
 * Resolves a workspace-relative path inside `workspaceRoot` while rejecting
 * absolute paths, drive letters, and `..` traversal. Mirrors the C#
 * `WorkspaceMaterializer.SanitizeRelativePath` behaviour so paths emitted
 * by the .NET bundle provider are accepted iff they pass the same gate.
 */
export function resolveSafeWorkspacePath(workspaceRoot: string, relative: string): string {
  if (!relative || relative.length === 0) {
    throw new Error("bootstrap file path must not be empty");
  }
  // Normalise to forward slashes; reject anything that re-introduces an
  // absolute prefix or a `..` segment after normalisation.
  const normalised = relative.replace(/\\/g, "/");
  if (path.isAbsolute(normalised) || normalised.includes(":")) {
    throw new Error(`bootstrap file path must be relative; got: ${relative}`);
  }
  const root = path.resolve(workspaceRoot);
  const resolved = path.resolve(root, normalised);
  if (resolved !== root && !resolved.startsWith(root + path.sep)) {
    throw new Error(`bootstrap file path escapes the workspace root: ${relative}`);
  }
  return resolved;
}
