"use client";

// Orphan-secret hygiene surface on the user-identity page (ADR-0047 §5).
//
// The OAuth flow's binding-wizard / user-identity intents both write a
// tenant secret named `binding/<bindingId-no-dash>/<connector>/pat`.
// When an operator abandons the wizard (or authorizes from the
// user-identity page without ever binding a unit through the wizard)
// the secret persists with no consumer. This surface lists the matching
// tenant secrets so the operator can clean them up.
//
// The "Forget" button calls `DELETE /api/v1/tenant/secrets/{name}` and
// invalidates the tenant-secrets cache slice. The card never shows the
// secret value — the wire never returns plaintext for tenant secrets.

import { useCallback, useState } from "react";
import { KeyRound, Loader2, Trash2 } from "lucide-react";
import { useQueryClient } from "@tanstack/react-query";

import { Button } from "@/components/ui/button";
import {
  Card,
  CardContent,
  CardHeader,
  CardTitle,
} from "@/components/ui/card";
import { ConfirmDialog } from "@/components/ui/confirm-dialog";
import { Skeleton } from "@/components/ui/skeleton";
import { useToast } from "@/components/ui/toast";
import { api } from "@/lib/api/client";
import { queryKeys } from "@/lib/api/query-keys";
import { formatTranslatedError } from "@/lib/api/translate-error";
import { useTenantSecrets } from "@/lib/api/queries";

// ADR-0047 §5 naming convention: `binding/<no-dash>/<slug>/pat`.
// We surface every connector here (v0.1 only `github` matches today),
// rather than hard-coding `/github/`, so a future connector that
// adopts the same naming convention slots in without a code change.
const ORPHAN_RE = /^binding\/[0-9a-f]{32}\/[a-z0-9-]+\/pat$/;

export interface OrphanSecretsPanelProps {
  // Reserved for cloud overlays that filter by tenant-user-scoped
  // secrets. v0.1 lists every match; the v0.2 hosted multi-user
  // overlay reopens this when scope narrows.
}

export function OrphanSecretsPanel(_props: OrphanSecretsPanelProps = {}) {
  const { toast } = useToast();
  const queryClient = useQueryClient();
  const secretsQuery = useTenantSecrets();
  const [pendingDelete, setPendingDelete] = useState<string | null>(null);
  const [deleting, setDeleting] = useState(false);

  const handleForget = useCallback(async () => {
    if (pendingDelete === null) return;
    setDeleting(true);
    try {
      await api.deleteTenantSecret(pendingDelete);
      await queryClient.invalidateQueries({
        queryKey: queryKeys.tenantSecrets.list(),
      });
      toast({ title: "Secret forgotten" });
      setPendingDelete(null);
    } catch (err) {
      const message = formatTranslatedError(err);
      toast({
        title: "Failed to delete tenant secret",
        description: message,
        variant: "destructive",
      });
    } finally {
      setDeleting(false);
    }
  }, [pendingDelete, queryClient, toast]);

  const matches = (secretsQuery.data ?? []).filter((s) =>
    ORPHAN_RE.test(s.name),
  );

  return (
    <Card data-testid="user-identity-orphan-secrets">
      <CardHeader className="gap-1">
        <CardTitle className="flex items-center gap-2 text-base">
          <KeyRound
            className="h-4 w-4 text-muted-foreground"
            aria-hidden="true"
          />
          Secrets pending binding
        </CardTitle>
        <p className="text-xs text-muted-foreground">
          OAuth-acquired tokens written by the wizard&apos;s
          authorize-then-bind flow live as tenant secrets under
          <code className="mx-1 rounded bg-muted px-1 py-0.5 text-[11px]">
            binding/&lt;id&gt;/&lt;connector&gt;/pat
          </code>
          (ADR-0047 §5). If you authorized but never finished the
          binding, the secret persists here for cleanup.
        </p>
      </CardHeader>
      <CardContent className="space-y-2">
        {secretsQuery.isLoading && (
          <div className="space-y-2">
            <Skeleton className="h-9" />
            <Skeleton className="h-9" />
          </div>
        )}
        {secretsQuery.isError && (
          <p
            role="alert"
            className="rounded-md border border-destructive/50 bg-destructive/10 px-3 py-2 text-xs text-destructive"
          >
            Failed to load tenant secrets: {formatTranslatedError(secretsQuery.error)}
          </p>
        )}
        {!secretsQuery.isLoading &&
          !secretsQuery.isError &&
          matches.length === 0 && (
            <p
              className="text-sm text-muted-foreground"
              data-testid="user-identity-orphan-secrets-empty"
            >
              No orphan binding secrets.
            </p>
          )}
        {matches.length > 0 && (
          <ul className="space-y-2" data-testid="user-identity-orphan-secrets-list">
            {matches.map((s) => (
              <li
                key={s.name}
                className="flex items-center justify-between gap-2 rounded-md border border-border bg-background px-3 py-2"
              >
                <div className="min-w-0 flex-1">
                  <p className="truncate font-mono text-xs">{s.name}</p>
                  <p className="text-[11px] text-muted-foreground">
                    Created {new Date(s.createdAt).toLocaleString()}
                  </p>
                </div>
                <Button
                  size="sm"
                  variant="outline"
                  onClick={() => setPendingDelete(s.name)}
                  data-testid={`user-identity-orphan-secret-forget-${s.name}`}
                >
                  <Trash2 className="mr-1 h-3 w-3" aria-hidden="true" />
                  Forget
                </Button>
              </li>
            ))}
          </ul>
        )}
        {pendingDelete !== null && (
          <ConfirmDialog
            open={pendingDelete !== null}
            title="Forget tenant secret"
            description={`Delete the tenant secret '${pendingDelete}'? Any binding still referencing this name will fail outbound calls until the operator re-binds.`}
            confirmLabel="Forget"
            onConfirm={handleForget}
            onCancel={() => setPendingDelete(null)}
            pending={deleting}
          />
        )}
      </CardContent>
    </Card>
  );
}
