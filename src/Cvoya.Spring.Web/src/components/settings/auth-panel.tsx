"use client";

// Auth panel (Settings drawer / #451). Scope for this PR:
//
// - Read-only "Signed in as …" line populated from `GET /api/v1/auth/me`.
//   When the OSS daemon runs without auth, the endpoint still returns
//   the `local-dev-user` identity; we surface that verbatim so OSS
//   operators see the local shell identity.
// - Read-only token list from `GET /api/v1/auth/tokens` — CLI parity
//   with `spring auth token list`.
// - A "Sign out" button that clears any in-memory API token decorator
//   state and reloads. OSS's default auth adapter has no state to
//   clear; hosted extensions will replace the adapter and attach their
//   own sign-out handler on the auth adapter itself (tracked by the
//   auth-adapter seam in #440).
//
// Token **create** and **revoke** controls are deferred to #557 so a
// shared "reveal once" primitive can land first — the CLI exposes
// both via `spring auth token {create,revoke}` today.

import { useAuthContext } from "@/lib/extensions";
import { useAuthTokens, useCurrentUser } from "@/lib/api/queries";

import { Button } from "@/components/ui/button";

export function AuthPanel() {
  const auth = useAuthContext();
  const userQuery = useCurrentUser();
  const tokensQuery = useAuthTokens();

  // Prefer the server's /auth/me response (carries the real display
  // name when hosted auth is wired); fall back to the extension auth
  // adapter's local user (OSS daemon's `local`). Never show both at
  // once — the adapter is the source of truth for the "signed in"
  // label on OSS.
  const serverUser = userQuery.data;
  const localUser = auth.getUser();
  const displayName =
    serverUser?.displayName ?? localUser?.displayName ?? "(not signed in)";
  const userId = serverUser?.userId ?? localUser?.id ?? null;

  const tokens = tokensQuery.data ?? [];

  const handleSignOut = () => {
    // The OSS auth adapter is stateless (daemon mode). A hosted auth
    // adapter would attach its own sign-out side effect on the adapter
    // itself via the extension seam (#440); this button hits that code
    // path and then reloads so the shell re-renders with the
    // default-adapter view.
    if (typeof window !== "undefined") {
      window.location.assign("/");
    }
  };

  return (
    <div className="space-y-4">
      <div>
        <div className="text-xs text-muted-foreground">Signed in as</div>
        <div className="text-sm font-medium" data-testid="settings-auth-user">
          {displayName}
        </div>
        {userId && userId !== displayName && (
          <div className="font-mono text-xs text-muted-foreground">
            {userId}
          </div>
        )}
      </div>

      <div>
        <div className="mb-1 flex items-baseline justify-between">
          <span className="text-xs text-muted-foreground">API tokens</span>
          <span className="text-[11px] text-muted-foreground">
            {tokensQuery.isPending ? "Loading…" : `${tokens.length} active`}
          </span>
        </div>
        {tokens.length === 0 ? (
          <p className="text-xs text-muted-foreground">
            No active tokens. Use{" "}
            <code className="font-mono text-[11px]">
              spring auth token create &lt;name&gt;
            </code>
            .
          </p>
        ) : (
          <ul
            className="divide-y divide-border rounded-md border border-border"
            data-testid="settings-auth-tokens"
          >
            {tokens.map((t) => (
              <li
                key={t.name}
                className="flex items-center justify-between gap-3 px-3 py-2 text-xs"
              >
                <span className="truncate font-medium">{t.name}</span>
                <span className="text-muted-foreground">
                  {formatCreatedAt(t.createdAt)}
                </span>
              </li>
            ))}
          </ul>
        )}
        <p className="mt-2 text-[11px] text-muted-foreground">
          Mirrors{" "}
          <code className="font-mono text-[11px]">spring auth token list</code>
          . Token create and revoke land in a follow-up (#557).
        </p>
      </div>

      <Button
        variant="outline"
        size="sm"
        onClick={handleSignOut}
        data-testid="settings-auth-signout"
      >
        Sign out
      </Button>
    </div>
  );
}

function formatCreatedAt(value: string | null | undefined): string {
  if (!value) return "";
  const date = new Date(value);
  if (Number.isNaN(date.getTime())) return "";
  // Short date form — the token list is not a temporal feed so the
  // detail in `/activity` timestamps would be overkill.
  return date.toLocaleDateString(undefined, {
    year: "numeric",
    month: "short",
    day: "numeric",
  });
}
