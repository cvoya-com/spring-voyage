"use client";

// Tenant defaults panel (Settings drawer / #615). Surfaces tenant-scoped
// secrets that act as inheritable defaults for every unit in the tenant
// — LLM API keys (anthropic-api-key, openai-api-key, google-api-key) are
// the primary use-case.
//
// Scope:
//  - OSS ships a narrow, fixed list of "known" credential names so the
//    panel can show "set / unset" status without an RBAC-sensitive call.
//    Operators who need to manage arbitrary tenant-scoped secrets reach
//    for `spring secret --scope tenant` on the CLI — the panel is
//    deliberately focused on the common LLM bootstrap path.
//  - Values never leave the server after submission; the form clears
//    them on success.
//  - Mirrors the Unit Secrets tab's ergonomic shape (set / rotate /
//    delete) so operators see one mental model across tenant and unit
//    scopes.
//
// CLI parity: every control maps 1:1 to `spring secret --scope tenant
// {create,rotate,delete}` (PR #612). The "set" button posts to POST
// (create) when the slot is empty and PUT (rotate) when it already
// exists — the CLI exposes both verbs.

import { useCallback, useEffect, useState } from "react";
import { KeyRound, RotateCw, Trash2 } from "lucide-react";

import { Badge } from "@/components/ui/badge";
import { Button } from "@/components/ui/button";
import { Input } from "@/components/ui/input";
import { useToast } from "@/components/ui/toast";
import { api } from "@/lib/api/client";
import type { SecretMetadata } from "@/lib/api/types";

/**
 * The fixed list of tier-2 LLM credentials the panel surfaces. New
 * providers append here — the list is intentionally short so the panel
 * stays an at-a-glance view of the inheritance-root credentials units
 * inherit from.
 */
const KNOWN_CREDENTIALS: ReadonlyArray<{
  name: string;
  label: string;
  description: string;
}> = [
  {
    name: "anthropic-api-key",
    label: "Anthropic API key",
    description:
      "Tenant default for the Anthropic (Claude) provider. Units inherit this unless they override with a same-name unit-scoped secret.",
  },
  {
    name: "openai-api-key",
    label: "OpenAI API key",
    description:
      "Tenant default for the OpenAI provider. Inherited by every unit in the tenant.",
  },
  {
    name: "google-api-key",
    label: "Google / Gemini API key",
    description:
      "Tenant default for the Google AI (Gemini) provider. Inherited by every unit in the tenant.",
  },
];

export function TenantDefaultsPanel() {
  const { toast } = useToast();
  const [secrets, setSecrets] = useState<SecretMetadata[]>([]);
  const [loading, setLoading] = useState(true);
  const [loadError, setLoadError] = useState<string | null>(null);
  const [submittingName, setSubmittingName] = useState<string | null>(null);

  const refresh = useCallback(async () => {
    try {
      const list = await api.listTenantSecrets();
      setSecrets(list.secrets ?? []);
      setLoadError(null);
    } catch (err) {
      const message = err instanceof Error ? err.message : String(err);
      setLoadError(message);
      setSecrets([]);
    }
  }, []);

  useEffect(() => {
    let cancelled = false;
    setLoading(true);
    refresh().finally(() => {
      if (!cancelled) setLoading(false);
    });
    return () => {
      cancelled = true;
    };
  }, [refresh]);

  const isSet = (name: string) =>
    secrets.some((s) => s.name === name);

  const setValue = async (name: string, value: string) => {
    setSubmittingName(name);
    try {
      // Create when the slot is empty, rotate (PUT) when it already
      // holds a value — the REST contract already disambiguates these.
      if (isSet(name)) {
        await api.rotateTenantSecret(name, { name, value });
        toast({ title: "Tenant default rotated", description: name });
      } else {
        await api.createTenantSecret({ name, value });
        toast({ title: "Tenant default set", description: name });
      }
      await refresh();
    } catch (err) {
      const message = err instanceof Error ? err.message : String(err);
      toast({
        title: "Save failed",
        description: message,
        variant: "destructive",
      });
    } finally {
      setSubmittingName(null);
    }
  };

  const deleteValue = async (name: string) => {
    setSubmittingName(name);
    try {
      await api.deleteTenantSecret(name);
      toast({ title: "Tenant default cleared", description: name });
      await refresh();
    } catch (err) {
      const message = err instanceof Error ? err.message : String(err);
      toast({
        title: "Delete failed",
        description: message,
        variant: "destructive",
      });
    } finally {
      setSubmittingName(null);
    }
  };

  return (
    <div className="space-y-4">
      <p className="text-xs text-muted-foreground">
        Tenant-default credentials every unit inherits. A unit can
        override any of these by adding a same-name entry on its
        <span className="font-medium"> Secrets </span>
        tab. Values are stored server-side and never returned to the
        browser.
      </p>

      {loadError && (
        <p className="rounded-md border border-destructive/50 bg-destructive/10 px-3 py-2 text-xs text-destructive">
          {loadError}
        </p>
      )}

      {loading ? (
        <p className="text-xs text-muted-foreground">Loading…</p>
      ) : (
        <ul
          className="divide-y divide-border rounded-md border border-border"
          data-testid="settings-tenant-defaults"
        >
          {KNOWN_CREDENTIALS.map((cred) => (
            <CredentialRow
              key={cred.name}
              name={cred.name}
              label={cred.label}
              description={cred.description}
              isSet={isSet(cred.name)}
              submitting={submittingName === cred.name}
              onSet={(value) => setValue(cred.name, value)}
              onDelete={() => deleteValue(cred.name)}
            />
          ))}
        </ul>
      )}

      <p className="text-[11px] text-muted-foreground">
        Need more than the fixed list above? Use{" "}
        <code className="font-mono text-[11px]">
          spring secret --scope tenant
        </code>{" "}
        for arbitrary tenant-scoped names.
      </p>
    </div>
  );
}

function CredentialRow({
  name,
  label,
  description,
  isSet,
  submitting,
  onSet,
  onDelete,
}: {
  name: string;
  label: string;
  description: string;
  isSet: boolean;
  submitting: boolean;
  onSet: (value: string) => void | Promise<void>;
  onDelete: () => void | Promise<void>;
}) {
  const [draft, setDraft] = useState("");

  const handleSet = () => {
    if (!draft) return;
    void onSet(draft);
    setDraft("");
  };

  return (
    <li
      className="space-y-2 px-3 py-3"
      data-testid={`tenant-default-${name}`}
    >
      <div className="flex items-start gap-2">
        <KeyRound className="mt-0.5 h-3.5 w-3.5 shrink-0 text-muted-foreground" />
        <div className="min-w-0 flex-1">
          <div className="flex items-center gap-2">
            <span className="text-xs font-medium">{label}</span>
            {isSet ? (
              <Badge variant="outline" className="text-[10px]">
                set
              </Badge>
            ) : (
              <Badge variant="outline" className="text-[10px] text-muted-foreground">
                unset
              </Badge>
            )}
          </div>
          <p className="font-mono text-[10px] text-muted-foreground">{name}</p>
          <p className="mt-1 text-[11px] text-muted-foreground">
            {description}
          </p>
        </div>
      </div>

      <div className="flex gap-2">
        <Input
          type="password"
          value={draft}
          onChange={(e) => setDraft(e.target.value)}
          placeholder={isSet ? "New value (rotates)" : "Value"}
          autoComplete="off"
          spellCheck={false}
          className="flex-1 text-xs"
        />
        <Button
          size="sm"
          variant="outline"
          disabled={!draft || submitting}
          onClick={handleSet}
          aria-label={isSet ? `Rotate ${label}` : `Set ${label}`}
        >
          {isSet ? (
            <>
              <RotateCw className="mr-1 h-3 w-3" /> Rotate
            </>
          ) : (
            "Set"
          )}
        </Button>
        {isSet && (
          <Button
            size="sm"
            variant="outline"
            disabled={submitting}
            onClick={() => void onDelete()}
            aria-label={`Clear ${label}`}
          >
            <Trash2 className="h-3 w-3" />
          </Button>
        )}
      </div>
    </li>
  );
}
