"use client";

// Connector identity card on the user-identity page (ADR-0047 §§ 2, 4).
//
// One card per registered connector. The card reads the connector's
// user-config schema (`GET /api/v1/tenant/connectors/{slug}/
// user-config-schema`) and renders form fields the schema describes. For
// GitHub that's `{ username (required), display_handle (optional) }`.
// Future connectors slot in by registering against the same schema-
// contribution seam; no per-connector storage code required here.
//
// The card is STRICTLY display-only. No PAT input, no installation id,
// no auth field of any kind. Outbound credentials live on unit
// bindings (ADR-0047 §11), acquired via the new-unit wizard's
// auth-choice sub-step or the CLI's `spring user identity
// authorize-github` verb. The "Authorize with GitHub" affordance on
// the GitHub card surfaces here because it refreshes the calling
// tenant user's display `username` — the token persists as a tenant
// secret for cleanup via the orphan-secret list, not on this row.

import { useCallback, useEffect, useMemo, useState } from "react";
import {
  CheckCircle2,
  Github,
  Loader2,
  Pencil,
  Plus,
  Trash2,
} from "lucide-react";
import { useQuery, useQueryClient } from "@tanstack/react-query";

import { Badge } from "@/components/ui/badge";
import { Button } from "@/components/ui/button";
import {
  Card,
  CardContent,
  CardHeader,
  CardTitle,
} from "@/components/ui/card";
import { ConfirmDialog } from "@/components/ui/confirm-dialog";
import { Input } from "@/components/ui/input";
import { Skeleton } from "@/components/ui/skeleton";
import { useToast } from "@/components/ui/toast";
import { api } from "@/lib/api/client";
import { queryKeys } from "@/lib/api/query-keys";
import { formatTranslatedError } from "@/lib/api/translate-error";
import type {
  TenantUserConnectorIdentityResponse,
} from "@/lib/api/types";
import {
  buildOAuthClientState,
  getAllowedOAuthCallbackOrigins,
  GH_OAUTH_CALLBACK_MESSAGE_TYPE,
  GH_OAUTH_CALLBACK_STORAGE_KEY,
  mintBindingId,
  parseOAuthCallbackPayload,
  parseStoredOAuthCallback,
} from "@connector-github/github-oauth-browser";

import {
  parseUserConfigSchema,
  readIdentityField,
  schemaFieldToRequestKey,
  type UserConfigField,
} from "./schema";

interface ConnectorIdentityCardProps {
  tenantUserId: string;
  connectorSlug: string;
  connectorDisplayName: string;
  /**
   * Existing identity row for this connector. `null` when the operator
   * has not configured a display identity yet — the card renders an
   * empty "Add identity" form.
   */
  identity: TenantUserConnectorIdentityResponse | null;
}

export function ConnectorIdentityCard({
  tenantUserId,
  connectorSlug,
  connectorDisplayName,
  identity,
}: ConnectorIdentityCardProps) {
  const { toast } = useToast();
  const queryClient = useQueryClient();

  // Per-connector schema fetch. v0.1 ships only GitHub; the card calls
  // the connector-scoped endpoint directly. Once a second connector
  // lands the slug→endpoint mapping moves into the API client; for now
  // we keep the wiring localised.
  const schemaQuery = useQuery({
    queryKey: ["connectors", "user-config-schema", connectorSlug],
    queryFn: async () => {
      if (connectorSlug === "github") {
        return await api.getGitHubUserConfigSchema();
      }
      return null;
    },
    staleTime: 5 * 60 * 1000,
  });

  const fields = useMemo<UserConfigField[]>(
    () => parseUserConfigSchema(schemaQuery.data),
    [schemaQuery.data],
  );

  const [editing, setEditing] = useState<boolean>(identity === null);
  const [values, setValues] = useState<Record<string, string>>({});
  const [submitting, setSubmitting] = useState(false);
  const [submitError, setSubmitError] = useState<string | null>(null);
  const [removing, setRemoving] = useState(false);
  const [confirmRemove, setConfirmRemove] = useState(false);

  // OAuth-driven `username` refresh (ADR-0047 §13). Only fires for
  // GitHub; the postMessage handoff carries the `login` claim from the
  // OAuth user-info response and we drop it into the `username` field.
  const [authorizing, setAuthorizing] = useState(false);
  const [awaitingCallback, setAwaitingCallback] = useState(false);
  const [authorizeError, setAuthorizeError] = useState<string | null>(null);

  // Seed form values whenever the identity row arrives / fields land.
  useEffect(() => {
    const next: Record<string, string> = {};
    for (const f of fields) {
      next[f.name] = readIdentityField(identity, f.name);
    }
    setValues(next);
  }, [fields, identity]);

  const handleSave = useCallback(async () => {
    setSubmitError(null);
    setSubmitting(true);
    try {
      const usernameKey = "username";
      const displayHandleKey = "display_handle";
      const username = (values[usernameKey] ?? "").trim();
      if (username === "") {
        // Defensive — required gate runs below too.
        throw new Error("Username is required.");
      }
      const displayHandle = (values[displayHandleKey] ?? "").trim();
      await api.upsertTenantUserIdentity(tenantUserId, {
        connectorId: connectorSlug,
        username,
        displayHandle: displayHandle === "" ? null : displayHandle,
      });
      await queryClient.invalidateQueries({
        queryKey: queryKeys.tenantUsers.identities(tenantUserId),
      });
      setEditing(false);
      toast({ title: "Identity saved" });
    } catch (err) {
      const message =
        err instanceof Error && err.message === "Username is required."
          ? err.message
          : formatTranslatedError(err);
      setSubmitError(message);
      toast({
        title: "Failed to save identity",
        description: message,
        variant: "destructive",
      });
    } finally {
      setSubmitting(false);
    }
  }, [connectorSlug, queryClient, tenantUserId, toast, values]);

  const handleRemove = useCallback(async () => {
    if (identity === null) return;
    setRemoving(true);
    try {
      await api.removeTenantUserIdentity(tenantUserId, {
        connectorId: connectorSlug,
        username: identity.username,
      });
      await queryClient.invalidateQueries({
        queryKey: queryKeys.tenantUsers.identities(tenantUserId),
      });
      setConfirmRemove(false);
      setEditing(true);
      toast({ title: "Identity removed" });
    } catch (err) {
      const message = formatTranslatedError(err);
      toast({
        title: "Failed to remove identity",
        description: message,
        variant: "destructive",
      });
    } finally {
      setRemoving(false);
    }
  }, [connectorSlug, identity, queryClient, tenantUserId, toast]);

  /**
   * GitHub-specific: opens the OAuth popup with the user-identity
   * intent. The callback page posts the OAuth `login` back; we drop
   * it into the `username` field for the operator to confirm.
   */
  const authorizeWithGitHub = useCallback(async () => {
    if (connectorSlug !== "github") return;
    setAuthorizeError(null);
    setAuthorizing(true);
    const popup = window.open(
      "",
      "spring-voyage-github-user-identity",
      "popup,width=720,height=760",
    );
    if (popup === null) {
      setAuthorizeError(
        "Your browser blocked the GitHub authorization window.",
      );
      setAuthorizing(false);
      return;
    }
    setAwaitingCallback(true);
    popup.focus();
    try {
      // The user-identity intent writes a tenant secret under a
      // transient binding UUID (ADR-0047 §5). The operator can forget
      // the secret later via the orphan-secrets list if it is unused.
      const transientBindingId = mintBindingId();
      const result = await api.beginGitHubOAuthAuthorize({
        clientState: buildOAuthClientState(),
        intent: "user-identity",
        tenantUserId,
        bindingId: transientBindingId,
      });
      popup.location.href = result.authorizeUrl;
    } catch (err) {
      popup.close();
      setAwaitingCallback(false);
      setAuthorizeError(formatTranslatedError(err));
    } finally {
      setAuthorizing(false);
    }
  }, [connectorSlug, tenantUserId]);

  useEffect(() => {
    if (connectorSlug !== "github") return;
    const allowedOrigins = getAllowedOAuthCallbackOrigins();
    const handlePayload = (value: unknown) => {
      const payload = parseOAuthCallbackPayload(value);
      if (payload === null) return;
      setAwaitingCallback(false);
      if (payload.error) {
        setAuthorizeError(payload.reason ?? payload.error);
        return;
      }
      if (payload.login) {
        setValues((prev) => ({ ...prev, username: payload.login as string }));
        setEditing(true);
      }
      // The server-side persister also refreshes the calling tenant
      // user's identity row for the user-identity intent
      // (ADR-0047 §13); invalidate the list so the read-side picks up
      // the latest write.
      void queryClient.invalidateQueries({
        queryKey: queryKeys.tenantUsers.identities(tenantUserId),
      });
      void queryClient.invalidateQueries({
        queryKey: queryKeys.tenantSecrets.list(),
      });
    };
    const handleMessage = (event: MessageEvent) => {
      if (!allowedOrigins.has(event.origin)) return;
      handlePayload(event.data);
    };
    const handleStorage = (event: StorageEvent) => {
      if (event.key !== GH_OAUTH_CALLBACK_STORAGE_KEY) return;
      const payload = parseStoredOAuthCallback(event.newValue);
      if (payload !== null) {
        handlePayload({
          ...payload,
          type: GH_OAUTH_CALLBACK_MESSAGE_TYPE,
        });
      }
    };

    window.addEventListener("message", handleMessage);
    window.addEventListener("storage", handleStorage);
    return () => {
      window.removeEventListener("message", handleMessage);
      window.removeEventListener("storage", handleStorage);
    };
  }, [connectorSlug, queryClient, tenantUserId]);

  const requiredSatisfied = useMemo(() => {
    for (const f of fields) {
      if (!f.required) continue;
      if ((values[f.name] ?? "").trim() === "") return false;
    }
    return true;
  }, [fields, values]);

  if (schemaQuery.isLoading) {
    return (
      <Card data-testid={`user-identity-card-${connectorSlug}`}>
        <CardContent className="space-y-3 p-6">
          <Skeleton className="h-4 w-32" />
          <Skeleton className="h-9" />
          <Skeleton className="h-9" />
        </CardContent>
      </Card>
    );
  }

  if (fields.length === 0) {
    // ADR-0047 §4: connectors without a display-identity concept
    // contribute an empty schema and render here as "no per-user
    // configuration".
    return (
      <Card data-testid={`user-identity-card-${connectorSlug}`}>
        <CardHeader>
          <CardTitle className="text-base">{connectorDisplayName}</CardTitle>
        </CardHeader>
        <CardContent>
          <p className="text-sm text-muted-foreground">
            No per-user configuration for this connector.
          </p>
        </CardContent>
      </Card>
    );
  }

  return (
    <Card data-testid={`user-identity-card-${connectorSlug}`}>
      <CardHeader className="gap-1">
        <CardTitle className="flex items-center gap-2 text-base">
          {connectorSlug === "github" && (
            <Github
              className="h-4 w-4 text-muted-foreground"
              aria-hidden="true"
            />
          )}
          {connectorDisplayName}
          {identity !== null && !editing && (
            <Badge variant="outline" className="ml-2">
              <CheckCircle2
                className="mr-1 h-3 w-3"
                aria-hidden="true"
              />
              Configured
            </Badge>
          )}
        </CardTitle>
        <p className="text-xs text-muted-foreground">
          Display identity only. The auth credential this unit pushes
          with lives on the unit binding, not here.
        </p>
      </CardHeader>
      <CardContent className="space-y-3">
        {!editing && identity !== null && (
          <dl
            className="space-y-2 text-sm"
            data-testid={`user-identity-readonly-${connectorSlug}`}
          >
            {fields.map((f) => {
              const value = readIdentityField(identity, f.name);
              return (
                <div key={f.name} className="space-y-1">
                  <dt className="text-xs uppercase tracking-wide text-muted-foreground">
                    {f.label}
                  </dt>
                  <dd className="font-mono text-sm">
                    {value === "" ? (
                      <span className="text-muted-foreground">(not set)</span>
                    ) : (
                      value
                    )}
                  </dd>
                </div>
              );
            })}
            <div className="flex flex-wrap gap-2 pt-2">
              <Button
                size="sm"
                variant="outline"
                onClick={() => setEditing(true)}
                data-testid={`user-identity-edit-${connectorSlug}`}
              >
                <Pencil className="mr-1 h-3 w-3" aria-hidden="true" />
                Edit
              </Button>
              <Button
                size="sm"
                variant="outline"
                onClick={() => setConfirmRemove(true)}
                data-testid={`user-identity-remove-${connectorSlug}`}
              >
                <Trash2 className="mr-1 h-3 w-3" aria-hidden="true" />
                Remove
              </Button>
            </div>
          </dl>
        )}

        {editing && (
          <form
            className="space-y-3"
            onSubmit={(e) => {
              e.preventDefault();
              void handleSave();
            }}
            data-testid={`user-identity-form-${connectorSlug}`}
          >
            {fields.map((f) => {
              const requestKey = schemaFieldToRequestKey(f.name);
              const inputId = `user-identity-${connectorSlug}-${f.name}`;
              return (
                <div key={f.name} className="space-y-1">
                  <label
                    htmlFor={inputId}
                    className="text-xs font-medium text-foreground"
                  >
                    {f.label}
                    {f.required && (
                      <span className="text-destructive"> *</span>
                    )}
                  </label>
                  <Input
                    id={inputId}
                    type="text"
                    value={values[f.name] ?? ""}
                    onChange={(e) =>
                      setValues((prev) => ({
                        ...prev,
                        [f.name]: e.target.value,
                      }))
                    }
                    data-testid={`user-identity-input-${connectorSlug}-${f.name}`}
                    aria-label={f.label}
                    placeholder={
                      requestKey === "username"
                        ? "octocat"
                        : requestKey === "displayHandle"
                          ? "Octocat (@octocat)"
                          : undefined
                    }
                  />
                  {f.description && (
                    <p className="text-[11px] text-muted-foreground">
                      {f.description}
                    </p>
                  )}
                </div>
              );
            })}

            {connectorSlug === "github" && (
              <div
                className="space-y-1 rounded-md border border-border bg-muted/40 p-2 text-xs"
                data-testid="user-identity-github-authorize"
              >
                <p className="font-medium">Refresh from GitHub OAuth</p>
                <p className="text-muted-foreground">
                  Authorize with GitHub to pull your login into the
                  username field automatically. The OAuth token is
                  written to a tenant secret; you can clean it up later
                  via the orphan-secrets list below if it goes unused.
                </p>
                <div className="flex flex-wrap items-center gap-2">
                  <Button
                    type="button"
                    size="sm"
                    variant="outline"
                    onClick={() => void authorizeWithGitHub()}
                    disabled={authorizing}
                    aria-busy={authorizing}
                    data-testid="user-identity-github-authorize-button"
                  >
                    {authorizing ? (
                      <Loader2 className="mr-1 h-3 w-3 animate-spin" />
                    ) : (
                      <Github className="mr-1 h-3 w-3" />
                    )}
                    {authorizing ? "Opening…" : "Authorize with GitHub"}
                  </Button>
                  {awaitingCallback && (
                    <span className="text-[11px] text-muted-foreground">
                      Finish authorization in the GitHub window. The
                      username field updates automatically.
                    </span>
                  )}
                </div>
                {authorizeError && (
                  <p className="text-[11px] text-destructive">
                    {authorizeError}
                  </p>
                )}
              </div>
            )}

            {submitError && (
              <p
                role="alert"
                className="rounded-md border border-destructive/50 bg-destructive/10 px-3 py-2 text-xs text-destructive"
              >
                {submitError}
              </p>
            )}

            <div className="flex flex-wrap gap-2">
              <Button
                type="submit"
                size="sm"
                disabled={submitting || !requiredSatisfied}
                data-testid={`user-identity-save-${connectorSlug}`}
              >
                {submitting ? (
                  <Loader2 className="mr-1 h-3 w-3 animate-spin" />
                ) : (
                  <Plus className="mr-1 h-3 w-3" />
                )}
                {submitting ? "Saving…" : "Save"}
              </Button>
              {identity !== null && (
                <Button
                  type="button"
                  size="sm"
                  variant="outline"
                  onClick={() => setEditing(false)}
                  disabled={submitting}
                >
                  Cancel
                </Button>
              )}
            </div>
          </form>
        )}

        {confirmRemove && identity !== null && (
          <ConfirmDialog
            open={confirmRemove}
            title="Remove connector identity"
            description={`Remove the ${connectorDisplayName} identity '${identity.username}' from your tenant user? This does not delete any tenant secret the binding still uses.`}
            confirmLabel="Remove"
            onConfirm={handleRemove}
            onCancel={() => setConfirmRemove(false)}
            pending={removing}
          />
        )}
      </CardContent>
    </Card>
  );
}
