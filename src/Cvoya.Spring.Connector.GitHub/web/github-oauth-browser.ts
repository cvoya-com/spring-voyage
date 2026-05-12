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
