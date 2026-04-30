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
 *      `/api/*` to the API host in `deployment/Caddyfile`).
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

/** GET a path. Throws ApiError on non-2xx. */
export const apiGet = <T>(path: string) => request<T>("GET", path);

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
// High-level helpers used by fixtures + cleanup.
// ---------------------------------------------------------------------------

/**
 * Best-effort delete a unit by name (the API accepts name OR id on the
 * `{id}` route segment). Cascades through memberships server-side.
 *
 * `force=true` adds `?force=true` so cleanup can wipe units stuck in
 * non-terminal states (Validating, Starting, Running, Stopping). Wizard
 * flows that interrupt validation can leave such units behind.
 */
export async function deleteUnit(name: string, force = true): Promise<void> {
  const q = force ? "?force=true" : "";
  await apiDelete(`/api/v1/tenant/units/${encodeURIComponent(name)}${q}`);
}

/** Best-effort delete an agent by id/name. */
export async function deleteAgent(id: string): Promise<void> {
  await apiDelete(`/api/v1/tenant/agents/${encodeURIComponent(id)}`);
}

/** Best-effort revoke an API token by name. */
export async function revokeToken(name: string): Promise<void> {
  await apiDelete(`/api/v1/tenant/auth/tokens/${encodeURIComponent(name)}`);
}

/** Best-effort delete a tenant-scoped secret. */
export async function deleteTenantSecret(name: string): Promise<void> {
  await apiDelete(`/api/v1/tenant/secrets/${encodeURIComponent(name)}`);
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
 * List units the suite owns (prefix match on the canonical run prefix).
 * Used by the sweep script to find orphans across runs.
 */
export async function listOwnedUnits(prefix: string): Promise<{ name: string }[]> {
  type UnitListItem = { name: string };
  const list = await apiGet<UnitListItem[]>("/api/v1/tenant/units");
  return list.filter((u) => u.name.startsWith(`${prefix}-`));
}

export async function listOwnedAgents(prefix: string): Promise<{ id: string; name: string }[]> {
  type AgentListItem = { id: string; name: string };
  const list = await apiGet<AgentListItem[]>("/api/v1/tenant/agents");
  return list.filter((a) => a.name.startsWith(`${prefix}-`) || a.id.startsWith(`${prefix}-`));
}

export async function listOwnedTokens(prefix: string): Promise<{ name: string }[]> {
  type TokenListItem = { name: string };
  const list = await apiGet<TokenListItem[]>("/api/v1/tenant/auth/tokens");
  return list.filter((t) => t.name.startsWith(`${prefix}-`));
}

export async function listOwnedTenantSecrets(prefix: string): Promise<{ name: string }[]> {
  type SecretListItem = { name: string };
  const list = await apiGet<SecretListItem[]>("/api/v1/tenant/secrets");
  return list.filter((s) => s.name.startsWith(`${prefix}-`));
}
