"use client";

// Browser-side bridge for the GitHub OAuth popup callback. The API callback
// page posts this message back to whichever portal surface opened it, and
// also drops the same payload into localStorage for same-origin fallback.

export const GH_OAUTH_SESSION_STORAGE_KEY =
  "springvoyage:github-oauth-session-id";
export const GH_OAUTH_CALLBACK_STORAGE_KEY =
  "springvoyage:github-oauth-callback";
export const GH_OAUTH_CALLBACK_MESSAGE_TYPE =
  "spring-voyage:github-oauth-session";

export interface GitHubOAuthCallbackPayload {
  sessionId?: string;
  login?: string;
  /**
   * ADR-0047 §13: the binding-scoped tenant secret the OAuth
   * token-persister wrote (`binding/<bindingId-no-dash>/github/pat`)
   * when the callback ran. Present on `binding-wizard` and
   * `user-identity` intents; absent on the legacy session-only flow.
   * The wizard's auth-choice sub-step reads this off the handoff and
   * wires it into the binding-create call's `pat_secret_name`.
   */
  patSecretName?: string;
  /**
   * ADR-0047 §13: the binding id the OAuth token-persister scoped the
   * secret to. For the wizard intent this echoes the UUID the wizard
   * pre-minted client-side; for the user-identity intent it is a
   * transient UUID the operator can clean up via the orphan-secrets
   * surface on `/settings/user-identity`.
   */
  bindingId?: string;
  error?: string;
  reason?: string;
}

export function readStoredOAuthSessionId(): string | null {
  if (typeof window === "undefined") return null;
  try {
    return window.sessionStorage.getItem(GH_OAUTH_SESSION_STORAGE_KEY);
  } catch {
    return null;
  }
}

export function writeStoredOAuthSessionId(value: string | null): void {
  if (typeof window === "undefined") return;
  try {
    if (value === null) {
      window.sessionStorage.removeItem(GH_OAUTH_SESSION_STORAGE_KEY);
    } else {
      window.sessionStorage.setItem(GH_OAUTH_SESSION_STORAGE_KEY, value);
    }
  } catch {
    // sessionStorage may be unavailable in embedded contexts.
  }
}

export function buildOAuthClientState(): string | null {
  if (typeof window === "undefined") return null;
  return JSON.stringify({ targetOrigin: window.location.origin });
}

/**
 * Pre-mints a no-dash UUID for use as the wizard's `bindingId` per
 * ADR-0047 §13. The wizard hands this UUID to the OAuth start endpoint;
 * the callback writes its secret under `binding/<no-dash>/github/pat`
 * and the binding-create call later reuses the same UUID so the secret
 * stays addressable. `crypto.randomUUID()` returns the dashed form; we
 * strip the dashes to match the server-side naming convention exactly
 * (the no-dash form is also what `OssTenantUserIds.OperatorNoDash` and
 * the binding-scoped secret-name format use).
 */
export function mintBindingId(): string {
  if (typeof crypto !== "undefined" && typeof crypto.randomUUID === "function") {
    return crypto.randomUUID().replace(/-/g, "");
  }
  // Fallback: should never fire in any browser the portal targets, but
  // satisfies the typecheck and produces a 32-char hex string with the
  // same shape so callers don't need to defensive-cast.
  return Array.from({ length: 32 }, () =>
    Math.floor(Math.random() * 16).toString(16),
  ).join("");
}

export function getAllowedOAuthCallbackOrigins(): Set<string> {
  const origins = new Set<string>();
  if (typeof window === "undefined") return origins;

  origins.add(window.location.origin);

  const apiBase = process.env.NEXT_PUBLIC_API_URL;
  if (apiBase) {
    try {
      origins.add(new URL(apiBase, window.location.href).origin);
    } catch {
      // Ignore malformed local config and fall back to the portal origin.
    }
  }

  return origins;
}

export function parseOAuthCallbackPayload(
  value: unknown,
): GitHubOAuthCallbackPayload | null {
  if (typeof value !== "object" || value === null) {
    return null;
  }

  const shaped = value as {
    type?: unknown;
    sessionId?: unknown;
    login?: unknown;
    patSecretName?: unknown;
    bindingId?: unknown;
    error?: unknown;
    reason?: unknown;
  };
  if (shaped.type !== GH_OAUTH_CALLBACK_MESSAGE_TYPE) {
    return null;
  }

  const sessionId =
    typeof shaped.sessionId === "string" && shaped.sessionId.trim() !== ""
      ? shaped.sessionId
      : undefined;
  const error =
    typeof shaped.error === "string" && shaped.error.trim() !== ""
      ? shaped.error
      : undefined;
  if (sessionId === undefined && error === undefined) {
    return null;
  }

  return {
    sessionId,
    login: typeof shaped.login === "string" ? shaped.login : undefined,
    patSecretName:
      typeof shaped.patSecretName === "string" &&
      shaped.patSecretName.trim() !== ""
        ? shaped.patSecretName
        : undefined,
    bindingId:
      typeof shaped.bindingId === "string" && shaped.bindingId.trim() !== ""
        ? shaped.bindingId
        : undefined,
    error,
    reason: typeof shaped.reason === "string" ? shaped.reason : undefined,
  };
}

export function parseStoredOAuthCallback(
  value: string | null,
): GitHubOAuthCallbackPayload | null {
  if (value === null) return null;
  try {
    return parseOAuthCallbackPayload(JSON.parse(value) as unknown);
  } catch {
    return null;
  }
}
