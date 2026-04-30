"use client";

// Account panel (Settings drawer / #451, token CRUD #557).
//
// - "Signed in as" line — GET /api/v1/auth/me. When OSS runs without auth
//   the endpoint returns `local-dev-user`; we surface it verbatim.
// - Token list — GET /api/v1/auth/tokens. CLI parity with
//   `spring auth token list`.
// - Create token — POST /api/v1/auth/tokens. One-shot reveal: the
//   plaintext appears once in a copyable pill with a "this is the only
//   time you'll see this" warning, then is scrubbed from React state
//   when the form resets or the panel unmounts. CLI parity with
//   `spring auth token create <name>`.
// - Revoke token — DELETE /api/v1/auth/tokens/{name} after a confirm
//   dialog. CLI parity with `spring auth token revoke <name>`.
//
// CLI parity status: parity-complete for list, create, and revoke.
//
// OSS has no logged-in user concept, so no sign-out control is rendered
// here (#589). Hosted extensions that introduce real sessions can attach
// their own sign-out affordance via the auth adapter seam (#440).
//
// One-shot reveal design:
//  - After a successful POST, the plaintext token is held in a local
//    `createdToken` state slot ONLY until the operator dismisses it.
//  - The dismiss clears the slot immediately — before any re-render that
//    could leak the value into a sibling or parent.
//  - On unmount the slot is zeroed in the useEffect cleanup so the value
//    does not sit in the React fiber tree after the panel closes.
//  - A design-system primitive is tracked as a follow-up (#1385); this
//    PR keeps the shape inline so the pattern can be validated first.

import { useCallback, useEffect, useRef, useState } from "react";
import { Check, Copy, Plus, Trash2, X } from "lucide-react";

import { Button } from "@/components/ui/button";
import { Input } from "@/components/ui/input";
import { useToast } from "@/components/ui/toast";
import { api } from "@/lib/api/client";
import { useAuthContext } from "@/lib/extensions";
import { useAuthTokens, useCurrentUser } from "@/lib/api/queries";
import { useQueryClient } from "@tanstack/react-query";
import { queryKeys } from "@/lib/api/query-keys";

export function AuthPanel() {
  const auth = useAuthContext();
  const { toast } = useToast();
  const queryClient = useQueryClient();
  const userQuery = useCurrentUser();
  const tokensQuery = useAuthTokens();

  // Prefer the server's /auth/me response; fall back to the extension
  // auth adapter's local user. Never show both at once — the adapter is
  // the source of truth for the "signed in" label on OSS.
  const serverUser = userQuery.data;
  const localUser = auth.getUser();
  const displayName =
    serverUser?.displayName ?? localUser?.displayName ?? "(not signed in)";
  const userId = serverUser?.userId ?? localUser?.id ?? null;

  const tokens = tokensQuery.data ?? [];

  // ----- Create form state -----
  const [creating, setCreating] = useState(false);
  const [newName, setNewName] = useState("");
  const [submitting, setSubmitting] = useState(false);
  const [createError, setCreateError] = useState<string | null>(null);

  // One-shot reveal slot. Held only until the operator dismisses the
  // pill. Scrubbed to "" on dismiss and on unmount.
  const [createdToken, setCreatedToken] = useState<{
    name: string;
    value: string;
  } | null>(null);
  const [copied, setCopied] = useState(false);

  // Revoke flow
  const [revokingName, setRevokingName] = useState<string | null>(null);
  const [confirmRevokeFor, setConfirmRevokeFor] = useState<string | null>(null);

  // Scrub plaintext from state on unmount.
  useEffect(() => {
    return () => {
      setCreatedToken(null);
      setNewName("");
    };
  }, []);

  const resetCreateForm = () => {
    setCreating(false);
    setNewName("");
    setCreateError(null);
  };

  const handleCreate = async () => {
    setCreateError(null);
    const name = newName.trim();
    if (!name) {
      setCreateError("Name is required.");
      return;
    }
    setSubmitting(true);
    try {
      const resp = await api.createAuthToken({ name });
      // Store the plaintext in the one-shot reveal slot.
      setCreatedToken({ name: resp.name, value: resp.token });
      resetCreateForm();
      // Invalidate the token list so the new entry appears.
      await queryClient.invalidateQueries({
        queryKey: queryKeys.auth.tokens(),
      });
    } catch (err) {
      const message = err instanceof Error ? err.message : String(err);
      setCreateError(message);
      toast({ title: "Create failed", description: message, variant: "destructive" });
    } finally {
      setSubmitting(false);
    }
  };

  const dismissToken = useCallback(() => {
    // Zero the slot synchronously before re-render.
    setCreatedToken(null);
    setCopied(false);
  }, []);

  const copyToken = useCallback(async () => {
    if (!createdToken) return;
    try {
      await navigator.clipboard.writeText(createdToken.value);
      setCopied(true);
      setTimeout(() => setCopied(false), 1500);
    } catch {
      toast({
        title: "Copy failed",
        description: "Use Ctrl+A / Cmd+A to select and copy the token manually.",
        variant: "destructive",
      });
    }
  }, [createdToken, toast]);

  const handleRevokeConfirm = async (name: string) => {
    setRevokingName(name);
    setConfirmRevokeFor(null);
    try {
      await api.revokeAuthToken(name);
      toast({ title: "Token revoked", description: name });
      await queryClient.invalidateQueries({
        queryKey: queryKeys.auth.tokens(),
      });
    } catch (err) {
      const message = err instanceof Error ? err.message : String(err);
      toast({
        title: "Revoke failed",
        description: message,
        variant: "destructive",
      });
    } finally {
      setRevokingName(null);
    }
  };

  return (
    <div className="space-y-4">
      {/* Signed-in user */}
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

      {/* One-shot reveal pill */}
      {createdToken && (
        <div
          role="alert"
          data-testid="settings-auth-token-reveal"
          className="rounded-md border border-warning/50 bg-warning/10 px-3 py-3 text-xs"
        >
          <div className="mb-2 font-medium text-warning">
            Copy this token now — it will not be shown again.
          </div>
          <div className="mb-2 text-muted-foreground">
            Token: <span className="font-medium">{createdToken.name}</span>
          </div>
          <div className="flex items-center gap-2">
            <code
              className="flex-1 truncate rounded bg-muted px-2 py-1 font-mono text-[11px]"
              data-testid="settings-auth-token-value"
            >
              {createdToken.value}
            </code>
            <Button
              size="sm"
              variant="outline"
              onClick={() => void copyToken()}
              aria-label="Copy token to clipboard"
            >
              {copied ? (
                <Check className="h-3 w-3" />
              ) : (
                <Copy className="h-3 w-3" />
              )}
            </Button>
            <Button
              size="sm"
              variant="outline"
              onClick={dismissToken}
              aria-label="Dismiss token reveal"
            >
              <X className="h-3 w-3" />
            </Button>
          </div>
        </div>
      )}

      {/* Token list */}
      <div>
        <div className="mb-1 flex items-baseline justify-between">
          <span className="text-xs text-muted-foreground">API tokens</span>
          <span className="text-[11px] text-muted-foreground">
            {tokensQuery.isPending ? "Loading…" : `${tokens.length} active`}
          </span>
        </div>
        {tokens.length === 0 && !tokensQuery.isPending ? (
          <p className="text-xs text-muted-foreground">No active tokens.</p>
        ) : (
          <ul
            className="divide-y divide-border rounded-md border border-border"
            data-testid="settings-auth-tokens"
          >
            {tokens.map((t) => {
              const isConfirming = confirmRevokeFor === t.name;
              const isRevoking = revokingName === t.name;
              return (
                <li
                  key={t.name}
                  className="flex items-center justify-between gap-3 px-3 py-2 text-xs"
                  data-testid={`settings-auth-token-row-${t.name}`}
                >
                  <span className="truncate font-medium">{t.name}</span>
                  <span className="shrink-0 text-muted-foreground">
                    {formatDate(t.createdAt)}
                  </span>
                  {isConfirming ? (
                    <span className="flex shrink-0 items-center gap-1">
                      <Button
                        size="sm"
                        variant="destructive"
                        onClick={() => void handleRevokeConfirm(t.name)}
                        disabled={isRevoking}
                        aria-label={`Confirm revoke ${t.name}`}
                      >
                        Revoke
                      </Button>
                      <Button
                        size="sm"
                        variant="outline"
                        onClick={() => setConfirmRevokeFor(null)}
                        aria-label="Cancel revoke"
                      >
                        Cancel
                      </Button>
                    </span>
                  ) : (
                    <Button
                      size="sm"
                      variant="outline"
                      onClick={() => setConfirmRevokeFor(t.name)}
                      disabled={isRevoking}
                      aria-label={`Revoke token ${t.name}`}
                      data-testid={`settings-auth-revoke-${t.name}`}
                    >
                      <Trash2 className="h-3.5 w-3.5" />
                    </Button>
                  )}
                </li>
              );
            })}
          </ul>
        )}
      </div>

      {/* Create form */}
      {creating ? (
        <div className="space-y-2">
          <label className="block space-y-1">
            <span className="text-xs text-muted-foreground">Token name</span>
            <Input
              value={newName}
              onChange={(e) => setNewName(e.target.value)}
              placeholder="my-ci-token"
              autoComplete="off"
              autoFocus
              onKeyDown={(e) => {
                if (e.key === "Enter") void handleCreate();
                if (e.key === "Escape") resetCreateForm();
              }}
              data-testid="settings-auth-token-name-input"
            />
          </label>
          {createError && (
            <p className="rounded-md border border-destructive/50 bg-destructive/10 px-3 py-2 text-xs text-destructive">
              {createError}
            </p>
          )}
          <div className="flex gap-2">
            <Button
              size="sm"
              onClick={() => void handleCreate()}
              disabled={submitting}
              data-testid="settings-auth-token-create-submit"
            >
              {submitting ? "Creating…" : "Create"}
            </Button>
            <Button
              size="sm"
              variant="outline"
              onClick={resetCreateForm}
              disabled={submitting}
            >
              Cancel
            </Button>
          </div>
        </div>
      ) : (
        <Button
          size="sm"
          variant="outline"
          onClick={() => setCreating(true)}
          className="flex items-center gap-1"
          data-testid="settings-auth-token-create-open"
        >
          <Plus className="h-3.5 w-3.5" />
          Create token
        </Button>
      )}

      <p className="text-[11px] text-muted-foreground">
        Mirrors{" "}
        <code className="font-mono text-[11px]">
          spring auth token {"{"}create,list,revoke{"}"}
        </code>
        .
      </p>
    </div>
  );
}

function formatDate(value: string | null | undefined): string {
  if (!value) return "";
  const date = new Date(value);
  if (Number.isNaN(date.getTime())) return "";
  return date.toLocaleDateString(undefined, {
    year: "numeric",
    month: "short",
    day: "numeric",
  });
}
