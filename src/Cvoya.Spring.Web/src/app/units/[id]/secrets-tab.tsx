"use client";

import { useCallback, useEffect, useMemo, useState } from "react";
import { KeyRound, Plus, Trash2 } from "lucide-react";

import { Badge } from "@/components/ui/badge";
import { Button } from "@/components/ui/button";
import {
  Card,
  CardContent,
  CardHeader,
  CardTitle,
} from "@/components/ui/card";
import { Input } from "@/components/ui/input";
import { useToast } from "@/components/ui/toast";
import { api } from "@/lib/api/client";
import type { SecretMetadata } from "@/lib/api/types";

interface SecretsTabProps {
  unitId: string;
}

type AddMode = "value" | "externalStoreKey";

/**
 * Unit-scoped secrets tab (#122). Shows the list of registered secret
 * names for this unit (server-supplied metadata only — no values or
 * store keys) and provides a form to add or delete secrets.
 *
 * Inheritance indicator (#615). Alongside the unit-scoped entries the
 * tab now fetches the tenant-scoped defaults and surfaces every name
 * that is visible to the unit only through inheritance, badged
 * "inherited from tenant". A unit-scoped entry with the same name
 * overrides the tenant default and is badged "set on unit" so
 * operators can see at a glance which tier is active.
 *
 * Security model:
 *  - Plaintext is held in local component state ONLY for the lifetime
 *    of the add form. It is cleared on successful submit and on tab
 *    unmount.
 *  - The server never returns the plaintext back; the list endpoint is
 *    metadata-only.
 *  - Agents and connectors read secrets through server-side
 *    ISecretResolver; the browser has no read path.
 */
export function SecretsTab({ unitId }: SecretsTabProps) {
  const { toast } = useToast();

  const [secrets, setSecrets] = useState<SecretMetadata[] | null>(null);
  const [tenantDefaults, setTenantDefaults] = useState<SecretMetadata[]>([]);
  const [loadError, setLoadError] = useState<string | null>(null);
  const [loading, setLoading] = useState(true);

  const [addMode, setAddMode] = useState<AddMode>("value");
  const [newName, setNewName] = useState("");
  const [newValue, setNewValue] = useState("");
  const [newExternalKey, setNewExternalKey] = useState("");
  const [submitError, setSubmitError] = useState<string | null>(null);
  const [submitting, setSubmitting] = useState(false);

  const [deletingName, setDeletingName] = useState<string | null>(null);

  const refresh = useCallback(async () => {
    try {
      const list = await api.listUnitSecrets(unitId);
      setSecrets(list.secrets ?? []);
      setLoadError(null);
    } catch (err) {
      const message = err instanceof Error ? err.message : String(err);
      setLoadError(message);
      setSecrets([]);
    }
    try {
      // Tenant defaults feed the inheritance indicator (#615). A
      // listing failure here is non-fatal — we simply render the
      // unit-scoped list without badges rather than surfacing an
      // error on the main card. The RBAC contract on the tenant
      // endpoint is independent of unit read access; OSS's
      // AllowAllSecretAccessPolicy allows both, the cloud host can
      // deny tenant reads to a unit operator.
      const tenantList = await api.listTenantSecrets();
      setTenantDefaults(tenantList.secrets ?? []);
    } catch {
      setTenantDefaults([]);
    }
  }, [unitId]);

  useEffect(() => {
    let cancelled = false;
    setLoading(true);
    refresh().finally(() => {
      if (!cancelled) setLoading(false);
    });
    return () => {
      cancelled = true;
      // Best-effort: zero out any plaintext held in state when the
      // tab unmounts, so it doesn't sit in the React fiber tree.
      setNewValue("");
      setNewExternalKey("");
    };
  }, [refresh]);

  const resetForm = () => {
    setNewName("");
    setNewValue("");
    setNewExternalKey("");
    setSubmitError(null);
  };

  const handleAdd = async () => {
    setSubmitError(null);

    if (!newName.trim()) {
      setSubmitError("Name is required.");
      return;
    }

    if (addMode === "value" && !newValue) {
      setSubmitError("Value is required.");
      return;
    }
    if (addMode === "externalStoreKey" && !newExternalKey.trim()) {
      setSubmitError("External store key is required.");
      return;
    }

    setSubmitting(true);
    try {
      await api.createUnitSecret(unitId, {
        name: newName.trim(),
        value: addMode === "value" ? newValue : undefined,
        externalStoreKey:
          addMode === "externalStoreKey" ? newExternalKey.trim() : undefined,
      });
      toast({ title: "Secret added", description: newName.trim() });
      resetForm();
      await refresh();
    } catch (err) {
      const message = err instanceof Error ? err.message : String(err);
      setSubmitError(message);
      toast({
        title: "Add failed",
        description: message,
        variant: "destructive",
      });
    } finally {
      setSubmitting(false);
    }
  };

  const handleDelete = async (name: string) => {
    setDeletingName(name);
    try {
      await api.deleteUnitSecret(unitId, name);
      toast({ title: "Secret deleted", description: name });
      await refresh();
    } catch (err) {
      const message = err instanceof Error ? err.message : String(err);
      toast({
        title: "Delete failed",
        description: message,
        variant: "destructive",
      });
    } finally {
      setDeletingName(null);
    }
  };

  // Merge unit-scoped + tenant-inherited entries for the display list.
  // Unit-scoped wins for the same name (override); tenant-only names
  // render read-only with an "inherited" badge.
  const displayRows = useMemo(() => {
    type Row = {
      name: string;
      origin: "unit" | "tenant";
      scope: SecretMetadata["scope"];
      createdAt: string;
      canDelete: boolean;
    };
    const unitNames = new Set((secrets ?? []).map((s) => s.name));
    const rows: Row[] = (secrets ?? []).map((s) => ({
      name: s.name,
      origin: "unit",
      scope: s.scope,
      createdAt: s.createdAt,
      canDelete: true,
    }));
    for (const td of tenantDefaults) {
      if (unitNames.has(td.name)) continue;
      rows.push({
        name: td.name,
        origin: "tenant",
        scope: td.scope,
        createdAt: td.createdAt,
        canDelete: false,
      });
    }
    return rows.sort((a, b) => a.name.localeCompare(b.name));
  }, [secrets, tenantDefaults]);

  return (
    <div className="space-y-4">
      <Card>
        <CardHeader>
          <CardTitle className="flex items-center gap-2">
            <KeyRound className="h-5 w-5" /> Secrets
          </CardTitle>
        </CardHeader>
        <CardContent className="space-y-3 text-sm">
          <p className="text-muted-foreground">
            Secrets are scoped to this unit. Values are stored server-side and
            never returned to the browser — this list shows metadata only.
            Entries marked{" "}
            <span className="font-medium">inherited from tenant</span>{" "}
            come from tenant defaults; add a same-name unit secret below to
            override for this unit only.
          </p>

          {loadError && (
            <p className="rounded-md border border-destructive/50 bg-destructive/10 px-3 py-2 text-destructive">
              {loadError}
            </p>
          )}

          {loading ? (
            <p className="text-muted-foreground">Loading…</p>
          ) : displayRows.length === 0 ? (
            <p className="text-muted-foreground">No secrets registered.</p>
          ) : (
            <ul className="divide-y divide-border rounded-md border border-border">
              {displayRows.map((s) => (
                <li
                  key={`${s.origin}:${s.name}`}
                  className="flex items-center gap-3 px-3 py-2"
                  data-testid={`unit-secret-row-${s.name}`}
                >
                  <span className="font-mono text-sm">{s.name}</span>
                  {s.origin === "unit" ? (
                    <Badge
                      variant="outline"
                      className="text-xs"
                      data-testid={`unit-secret-badge-${s.name}`}
                    >
                      set on unit
                    </Badge>
                  ) : (
                    <Badge
                      variant="outline"
                      className="text-xs text-muted-foreground"
                      data-testid={`unit-secret-badge-${s.name}`}
                    >
                      inherited from tenant
                    </Badge>
                  )}
                  <span className="ml-auto text-xs text-muted-foreground">
                    {new Date(s.createdAt).toLocaleString()}
                  </span>
                  <Button
                    size="sm"
                    variant="outline"
                    onClick={() => handleDelete(s.name)}
                    disabled={!s.canDelete || deletingName === s.name}
                    aria-label={`Delete ${s.name}`}
                    title={
                      s.canDelete
                        ? undefined
                        : "Inherited from tenant — clear via the Tenant defaults panel in Settings."
                    }
                  >
                    <Trash2 className="h-4 w-4" />
                  </Button>
                </li>
              ))}
            </ul>
          )}
        </CardContent>
      </Card>

      <Card>
        <CardHeader>
          <CardTitle className="flex items-center gap-2">
            <Plus className="h-5 w-5" /> Add secret
          </CardTitle>
        </CardHeader>
        <CardContent className="space-y-3 text-sm">
          <div className="flex gap-2">
            <Button
              size="sm"
              variant={addMode === "value" ? "default" : "outline"}
              onClick={() => setAddMode("value")}
            >
              Pass-through value
            </Button>
            <Button
              size="sm"
              variant={addMode === "externalStoreKey" ? "default" : "outline"}
              onClick={() => setAddMode("externalStoreKey")}
            >
              External reference
            </Button>
          </div>

          <label className="block space-y-1">
            <span className="text-sm text-muted-foreground">Name</span>
            <Input
              value={newName}
              onChange={(e) => setNewName(e.target.value)}
              placeholder="gh-token"
              autoComplete="off"
            />
          </label>

          {addMode === "value" ? (
            <label className="block space-y-1">
              <span className="text-sm text-muted-foreground">
                Value (stored server-side; never returned)
              </span>
              <Input
                type="password"
                value={newValue}
                onChange={(e) => setNewValue(e.target.value)}
                autoComplete="off"
                spellCheck={false}
              />
            </label>
          ) : (
            <label className="block space-y-1">
              <span className="text-sm text-muted-foreground">
                External store key
              </span>
              <Input
                value={newExternalKey}
                onChange={(e) => setNewExternalKey(e.target.value)}
                placeholder="kv://vault/secret-id"
                autoComplete="off"
              />
            </label>
          )}

          {submitError && (
            <p className="rounded-md border border-destructive/50 bg-destructive/10 px-3 py-2 text-destructive">
              {submitError}
            </p>
          )}

          <div className="flex justify-end">
            <Button onClick={handleAdd} disabled={submitting}>
              {submitting ? "Adding…" : "Add secret"}
            </Button>
          </div>
        </CardContent>
      </Card>
    </div>
  );
}
