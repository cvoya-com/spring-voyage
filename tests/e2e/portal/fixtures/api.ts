/**
 * Direct REST helpers for setup/teardown alongside browser flows.
 *
 * Why a parallel API client: Playwright drives the *user* path through the
 * browser, but suite-wide cleanup, pre-flight readiness checks, and
 * fixture seeding need a non-UI path. Going through the browser for
 * every cleanup step would multiply test time by orders of magnitude
 * and couple cleanup robustness to UI uptime — if the wizard crashes
 * mid-test, we still need to be able to delete the orphan unit.
 *
 * The helpers below are intentionally thin: minimal typing, no caching,
 * no retries. They mirror `src/Cvoya.Spring.Web/src/lib/api/client.ts`
 * but live independent of it because this suite is a standalone npm
 * package outside the workspace (avoids dragging the Next.js graph in).
 */

import { OLLAMA_BASE_URL } from "./runtime.js";

/**
 * API base URL. Resolution order:
 *   1. `SPRING_API_URL` — set explicitly to point at the API host.
 *   2. `PLAYWRIGHT_BASE_URL` — same origin as the portal (Caddy proxies
 *      `/api/*` to the API host in `eng/deploy/Caddyfile`).
 *   3. `http://localhost` — single-host docker-compose default.
 */
export const API_BASE_URL: string =
  process.env.SPRING_API_URL?.trim() ||
  process.env.PLAYWRIGHT_BASE_URL?.trim() ||
  "http://localhost";

const TOKEN = process.env.SPRING_API_TOKEN?.trim() || null;

function authHeaders(): Record<string, string> {
  return TOKEN ? { Authorization: `Bearer ${TOKEN}` } : {};
}

export class ApiError extends Error {
  constructor(
    public readonly status: number,
    public readonly statusText: string,
    public readonly body: string,
    public readonly url: string,
  ) {
    super(
      `API ${status} ${statusText} ${url}${body ? ` — ${body.slice(0, 500)}` : ""}`,
    );
    this.name = "ApiError";
  }
}

async function request<T>(
  method: string,
  path: string,
  init?: { body?: unknown; expect?: number[] },
): Promise<T> {
  const url = `${API_BASE_URL}${path}`;
  const expect = init?.expect ?? [200, 201, 202, 204];
  const res = await fetch(url, {
    method,
    headers: {
      ...authHeaders(),
      ...(init?.body !== undefined ? { "Content-Type": "application/json" } : {}),
    },
    body: init?.body !== undefined ? JSON.stringify(init.body) : undefined,
  });
  const text = await res.text();
  if (!expect.includes(res.status)) {
    throw new ApiError(res.status, res.statusText, text, url);
  }
  // 204 / 202 with empty body — return undefined as the typed value.
  if (!text) return undefined as T;
  try {
    return JSON.parse(text) as T;
  } catch {
    return text as unknown as T;
  }
}

/** GET a path. Throws ApiError on non-2xx (override via `expect`). */
export const apiGet = <T>(path: string, init?: { expect?: number[] }) =>
  request<T>("GET", path, init);

/** POST JSON. Throws ApiError on non-2xx. */
export const apiPost = <T>(path: string, body?: unknown) =>
  request<T>("POST", path, { body });

/** PUT JSON. Throws ApiError on non-2xx. */
export const apiPut = <T>(path: string, body?: unknown) =>
  request<T>("PUT", path, { body });

/** DELETE. 404 is acceptable (idempotent cleanup). */
export const apiDelete = (path: string) =>
  request<void>("DELETE", path, { expect: [200, 202, 204, 404] });

// ---------------------------------------------------------------------------
// Identity model (post-#2473 / address-by-hex reshape).
//
// Units and agents are addressed by their server-assigned id: the canonical
// `id` is the dashed UUID, `name` is the same id in the 32-char no-dash hex
// form, and the operator-chosen human-friendly string lives in `displayName`.
//
// The `name` field on the create requests is NOT a slug the server honours —
// it is overwritten with the hex. The suite therefore puts its run-scoped
// slug (`unitName()` / `agentName()`) into `displayName`, and threads the
// server-assigned hex back to callers for navigation (`/explorer/units/<hex>`)
// and child endpoints (`unitIds: [<hex>]`, `/units/<hex>/execution`, …).
//
// Cleanup and the orphan sweep therefore match owned artefacts on
// `displayName` (the slug) and delete by `id`/`name` (the hex).
// ---------------------------------------------------------------------------

/** Server identity for a created unit / agent. */
export interface SeededEntity {
  /** Dashed UUID (`id`). */
  id: string;
  /** 32-char no-dash hex (`name`) — canonical for URL paths and child routes. */
  hex: string;
  /** Operator-chosen display name (the suite's run-scoped slug). */
  displayName: string;
}

interface UnitCreateResponse {
  id: string;
  name: string;
  displayName: string;
}

interface AgentCreateResponse {
  id: string;
  name: string;
  displayName: string;
}

export interface SeedUnitOptions {
  description?: string;
  /** Server-assigned hex ids of parent units. Omit for a top-level unit. */
  parentHexIds?: string[];
  /** Free-text model id stamped on the unit row. */
  model?: string;
  hosting?: string;
}

/**
 * Create a unit via the public API with the current `CreateUnitRequest`
 * contract. The suite slug is passed as `displayName`; the server assigns
 * the hex id. Returns the server identity so the caller can navigate and
 * register the hex for cleanup.
 */
export async function seedUnit(
  slug: string,
  opts: SeedUnitOptions = {},
): Promise<SeededEntity> {
  const isTopLevel = !opts.parentHexIds || opts.parentHexIds.length === 0;
  const res = await apiPost<UnitCreateResponse>("/api/v1/tenant/units", {
    name: slug, // ignored by the server (overwritten with the hex) but harmless.
    displayName: slug,
    description: opts.description ?? `Created by e2e-portal: ${slug}`,
    model: opts.model,
    hosting: opts.hosting ?? "ephemeral",
    isTopLevel: isTopLevel ? true : undefined,
    parentUnitIds: isTopLevel ? undefined : opts.parentHexIds,
  });
  return { id: res.id, hex: res.name, displayName: res.displayName };
}

export interface SeedAgentOptions {
  description?: string;
  role?: string;
  /** Server-assigned hex ids of the units this agent joins. */
  unitHexIds: string[];
}

/**
 * Create an agent via the public API with the current `CreateAgentRequest`
 * contract: `{ displayName, description, role, unitIds }`. There is no
 * caller-supplied `name`/`id` (the server assigns the hex), and `unitIds`
 * MUST be server-assigned hex ids — display-name slugs are rejected as
 * invalid Guids. Returns the server identity.
 */
export async function seedAgent(
  slug: string,
  opts: SeedAgentOptions,
): Promise<SeededEntity> {
  const res = await apiPost<AgentCreateResponse>("/api/v1/tenant/agents", {
    displayName: slug,
    description: opts.description ?? `Created by e2e-portal: ${slug}`,
    role: opts.role ?? null,
    unitIds: opts.unitHexIds,
  });
  return { id: res.id, hex: res.name, displayName: res.displayName };
}

// ---------------------------------------------------------------------------
// High-level helpers used by fixtures + cleanup.
// ---------------------------------------------------------------------------

/**
 * Best-effort delete a unit by id (dashed UUID or 32-char hex). Cascades
 * through memberships server-side.
 *
 * `force=true` adds `?force=true` so cleanup can wipe units stuck in
 * non-terminal states (Validating, Starting, Running, Stopping). Wizard
 * flows that interrupt validation can leave such units behind.
 *
 * NOTE: the route keys on the hex/UUID, NOT the display-name slug — a slug
 * lands a 400 ("not a valid Guid"). Cleanup resolves slug → hex before
 * calling this (see `fixtures/test.ts`).
 */
export async function deleteUnit(idOrHex: string, force = true): Promise<void> {
  const q = force ? "?force=true" : "";
  await apiDelete(`/api/v1/tenant/units/${encodeURIComponent(idOrHex)}${q}`);
}

/** Best-effort delete an agent by id (dashed UUID or 32-char hex). */
export async function deleteAgent(idOrHex: string): Promise<void> {
  await apiDelete(`/api/v1/tenant/agents/${encodeURIComponent(idOrHex)}`);
}

/**
 * Make a unit (and the agents inside it) reachable from the test caller by
 * adding one of the caller's bound Hats as a TEAM member of the unit.
 *
 * Hat-reachability gate (#2972): a tenant-user may only message a unit /
 * agent if they wear a Hat (a bound `human://`) that is a team member of
 * the unit the target sits in. A freshly-created unit has no human members,
 * so a UI / API send to its agent returns 403 `NoReachableHat`. This mirrors
 * the CLI suite's `e2e::add_caller_hat`: resolve the caller's primary Hat
 * from `GET /users/me/humans`, then POST it as a unit member.
 *
 * Returns the humanId of the Hat that was added (or null when the tenant
 * exposes no caller Hat, e.g. an auth mode without a bound human — the
 * caller should then `test.skip`).
 */
export async function addCallerHat(unitHex: string): Promise<string | null> {
  type MeHuman = { humanId: string; isPrimary?: boolean };
  const humans = await apiGet<MeHuman[]>("/api/v1/tenant/users/me/humans").catch(
    () => [] as MeHuman[],
  );
  const first = humans?.[0];
  if (!first) return null;
  const humanId = (humans.find((h) => h.isPrimary) ?? first).humanId;
  await apiPost(`/api/v1/tenant/units/${encodeURIComponent(unitHex)}/members/humans`, {
    humanId,
    roles: ["owner"],
  });
  return humanId;
}

/** Best-effort revoke an API token by name. */
export async function revokeToken(name: string): Promise<void> {
  await apiDelete(`/api/v1/tenant/auth/tokens/${encodeURIComponent(name)}`);
}

/** Best-effort delete a tenant-scoped secret. */
export async function deleteTenantSecret(name: string): Promise<void> {
  await apiDelete(`/api/v1/tenant/secrets/${encodeURIComponent(name)}`);
}

/**
 * Determine whether a catalog package can be installed in this
 * (credential-free) environment, i.e. it has no unsatisfied required
 * credential and no required connector binding.
 *
 * Every unit-installing OSS package now pins `runtime: claude-code`
 * (declaring a required `anthropic-oauth` credential) and the engineering
 * / PM packages additionally declare a required `github` connector. The
 * install pipeline fails-fast (400) when either is missing, so the
 * portal's credential-free suite (dapr-agent + ollama, no operator
 * secrets) cannot drive those installs to completion. Returns a reason
 * string when the package is NOT installable here, or null when it is.
 */
export async function packageInstallBlockedReason(
  packageName: string,
): Promise<string | null> {
  type RequiredCredsResponse = {
    required: { provider: string; authMethod: string; secretName: string }[];
  };
  type PackageDetail = {
    connectorDeclarations?: { type?: string | null; required?: boolean }[] | null;
  };
  const creds = await apiGet<RequiredCredsResponse>(
    `/api/v1/tenant/packages/${encodeURIComponent(packageName)}/required-credentials`,
  ).catch(() => ({ required: [] as RequiredCredsResponse["required"] }));
  if (creds.required.length > 0) {
    const names = creds.required.map((c) => c.secretName).join(", ");
    return `package '${packageName}' requires unsatisfied credential(s): ${names}`;
  }
  const detail = await apiGet<PackageDetail>(
    `/api/v1/tenant/packages/${encodeURIComponent(packageName)}`,
  ).catch(() => ({}) as PackageDetail);
  const requiredConnectors = (detail.connectorDeclarations ?? [])
    .filter((d) => d.required)
    .map((d) => d.type)
    .filter((t): t is string => typeof t === "string" && t.length > 0);
  if (requiredConnectors.length > 0) {
    return `package '${packageName}' requires connector binding(s): ${requiredConnectors.join(", ")}`;
  }
  return null;
}

// ---------------------------------------------------------------------------
// Readiness probes — surface a clear skip-message when the local stack or
// the LLM backend isn't up. Mirrors `e2e::require_ollama` from _lib.sh.
// ---------------------------------------------------------------------------

export async function isApiUp(): Promise<boolean> {
  try {
    const res = await fetch(`${API_BASE_URL}/health`, { method: "GET" });
    return res.ok;
  } catch {
    return false;
  }
}

export async function isOllamaUp(): Promise<boolean> {
  try {
    const res = await fetch(`${OLLAMA_BASE_URL.replace(/\/$/, "")}/api/tags`, {
      method: "GET",
    });
    return res.ok;
  } catch {
    return false;
  }
}

/**
 * List units the suite owns. The suite slug now lives in `displayName`
 * (the `name` field is the server-assigned hex), so match on `displayName`.
 * Returns the dashed `id` for deletion (the route keys on hex/UUID, not the
 * slug). Used by the sweep script + cleanup hook to find orphans across runs.
 */
export async function listOwnedUnits(
  prefix: string,
): Promise<{ id: string; hex: string; displayName: string }[]> {
  type UnitListItem = { id: string; name: string; displayName: string };
  const list = await apiGet<UnitListItem[]>("/api/v1/tenant/units");
  return list
    .filter((u) => (u.displayName ?? "").startsWith(`${prefix}-`))
    .map((u) => ({ id: u.id, hex: u.name, displayName: u.displayName }));
}

export async function listOwnedAgents(
  prefix: string,
): Promise<{ id: string; hex: string; displayName: string }[]> {
  type AgentListItem = { id: string; name: string; displayName: string };
  const list = await apiGet<AgentListItem[]>("/api/v1/tenant/agents");
  return list
    .filter((a) => (a.displayName ?? "").startsWith(`${prefix}-`))
    .map((a) => ({ id: a.id, hex: a.name, displayName: a.displayName }));
}

/**
 * Resolve a unit's display-name slug to its server-assigned dashed id.
 * Returns null when no owned unit carries that display name (already
 * deleted, or never created). Cleanup uses this to delete by id.
 */
export async function resolveUnitIdByDisplayName(
  displayName: string,
): Promise<string | null> {
  type UnitListItem = { id: string; displayName: string };
  const list = await apiGet<UnitListItem[]>("/api/v1/tenant/units");
  return list.find((u) => u.displayName === displayName)?.id ?? null;
}

/** Resolve an agent's display-name slug to its server-assigned dashed id. */
export async function resolveAgentIdByDisplayName(
  displayName: string,
): Promise<string | null> {
  type AgentListItem = { id: string; displayName: string };
  const list = await apiGet<AgentListItem[]>("/api/v1/tenant/agents");
  return list.find((a) => a.displayName === displayName)?.id ?? null;
}

export async function listOwnedTokens(prefix: string): Promise<{ name: string }[]> {
  type TokenListItem = { name: string };
  const list = await apiGet<TokenListItem[]>("/api/v1/tenant/auth/tokens");
  return list.filter((t) => t.name.startsWith(`${prefix}-`));
}

export async function listOwnedTenantSecrets(prefix: string): Promise<{ name: string }[]> {
  type SecretListItem = { name: string };
  // The API wraps the list in `{ secrets: [...] }` (TenantSecretListResponse).
  const response = await apiGet<{ secrets: SecretListItem[] } | SecretListItem[]>(
    "/api/v1/tenant/secrets",
  );
  const list = Array.isArray(response) ? response : (response.secrets ?? []);
  return list.filter((s) => s.name.startsWith(`${prefix}-`));
}
