"use client";

import { Eye, EyeOff, KeyRound, Plug } from "lucide-react";
import { useMemo, useState } from "react";

import { Button } from "@/components/ui/button";
import { Input } from "@/components/ui/input";
import { Skeleton } from "@/components/ui/skeleton";
import {
  useProviderCredentialStatus,
  usePackageRequiredCredentials,
} from "@/lib/api/queries";
import { formatTranslatedError } from "@/lib/api/translate-error";
import type { PackageRequiredCredentialEntryResponse } from "@/lib/api/types";

/**
 * #2181: pre-emptive credential-requirements panel for the catalog
 * install wizard. Renders one input per `(provider, authMethod)` edge
 * a member unit consumes and that the tenant secret store doesn't
 * already satisfy. The same backend resolver the install pre-flight
 * uses is queried via
 * <c>GET /api/v1/tenant/packages/{name}/required-credentials</c>, so
 * the wizard and the install pipeline agree on what's required.
 *
 * Renders nothing when the package has no LLM requirement (Ollama-only
 * packages, custom runtimes) or when the tenant already has every edge
 * satisfied. Survives the install round-trip alongside the existing
 * <c>{@link CredentialsMissingRetryForm}</c> reactive surface, which
 * stays as the fallback for races (operator removed the secret between
 * page load and click).
 */
export interface PackageCredentialRequirementsPanelProps {
  packageName: string | null;
  /**
   * Operator-typed credential values keyed by `${provider}:${authMethod}`.
   * Owned by the parent so values survive package-selection changes
   * within a single wizard session and so the install button can
   * read them at submit time.
   */
  values: Record<string, string>;
  onValuesChange: (next: Record<string, string>) => void;
}

export function PackageCredentialRequirementsPanel({
  packageName,
  values,
  onValuesChange,
}: PackageCredentialRequirementsPanelProps) {
  const requiredQuery = usePackageRequiredCredentials(packageName ?? "", {
    enabled: Boolean(packageName),
  });
  const required = useMemo(
    () => requiredQuery.data?.required ?? [],
    [requiredQuery.data],
  );

  if (!packageName) return null;

  if (requiredQuery.isPending) {
    return (
      <div
        role="status"
        aria-label="Loading credential requirements"
        data-testid="package-credential-requirements"
        className="rounded-md border border-border bg-muted/30 px-3 py-2"
      >
        <div className="flex items-start gap-2">
          <Skeleton className="mt-0.5 h-4 w-4 shrink-0 rounded-full" />
          <div className="flex-1 space-y-2">
            <Skeleton className="h-4 w-40" />
            <Skeleton className="h-3 w-64 max-w-full" />
          </div>
        </div>
      </div>
    );
  }

  if (requiredQuery.isError) {
    // Soft-fail: the reactive `<CredentialsMissingRetryForm>` still
    // catches missing credentials at install time. The pre-emptive
    // panel's job is operator UX, not a hard gate.
    return (
      <p
        role="status"
        data-testid="package-credential-requirements"
        className="text-xs text-muted-foreground"
      >
        Could not load credential requirements:{" "}
        {formatTranslatedError(requiredQuery.error)}
      </p>
    );
  }

  if (required.length === 0) {
    return null;
  }

  return (
    <RequirementList
      packageName={packageName}
      required={required}
      values={values}
      onValuesChange={onValuesChange}
    />
  );
}

function RequirementList({
  packageName,
  required,
  values,
  onValuesChange,
}: {
  packageName: string;
  required: PackageRequiredCredentialEntryResponse[];
  values: Record<string, string>;
  onValuesChange: (next: Record<string, string>) => void;
}) {
  return (
    <div
      role="status"
      data-testid="package-credential-requirements"
      data-package-name={packageName}
      className="rounded-md border border-primary/40 bg-primary/10 px-3 py-2 text-sm text-foreground"
    >
      <div className="flex items-start gap-2">
        <Plug className="mt-0.5 h-4 w-4 shrink-0 text-primary" aria-hidden />
        <div className="min-w-0 flex-1">
          <p className="font-medium">Credential requirements</p>
          <p className="mt-0.5 text-xs text-muted-foreground">
            This package needs the following credentials. Anything you paste
            below is saved as a tenant secret during install so it doesn&apos;t
            block again next time.
          </p>
          <ul className="mt-2 space-y-3">
            {required.map((entry) => (
              <li key={entryKey(entry)}>
                <RequirementRow
                  entry={entry}
                  value={values[entryKey(entry)] ?? ""}
                  onChange={(next) =>
                    onValuesChange({
                      ...values,
                      [entryKey(entry)]: next,
                    })
                  }
                />
              </li>
            ))}
          </ul>
        </div>
      </div>
    </div>
  );
}

function RequirementRow({
  entry,
  value,
  onChange,
}: {
  entry: PackageRequiredCredentialEntryResponse;
  value: string;
  onChange: (next: string) => void;
}) {
  // Probe tenant satisfaction. When the secret resolves at tenant
  // scope we hide the input — operator already gave us this once.
  // The reactive retry form picks up the rare race where the
  // secret was removed between this probe and the install POST.
  const status = useProviderCredentialStatus(entry.provider, {
    authMethod: entry.authMethod === "oauth" ? "oauth" : "api-key",
  });
  const [show, setShow] = useState(false);

  const tenantSatisfies = status.data?.resolvable === true;
  if (tenantSatisfies) {
    return (
      <p
        data-testid={`package-credential-requirement-resolved-${entry.provider}-${entry.authMethod}`}
        className="text-xs text-muted-foreground"
      >
        <code className="rounded bg-muted px-1 py-0.5 font-mono text-[11px]">
          {entry.credentialEnvVar}
        </code>{" "}
        — already configured at tenant scope. No action required.
      </p>
    );
  }

  const inputId = `package-credential-input-${entry.provider}-${entry.authMethod}`;

  return (
    <div data-testid={`package-credential-requirement-${entry.provider}-${entry.authMethod}`}>
      <label htmlFor={inputId} className="block space-y-1">
        <span className="flex items-center gap-1.5 text-xs font-medium text-foreground">
          <KeyRound className="h-3 w-3 text-muted-foreground" aria-hidden />
          <code className="rounded bg-muted px-1 py-0.5 font-mono text-[11px]">
            {entry.credentialEnvVar}
          </code>
          <span className="text-muted-foreground">
            ({entry.provider} / {entry.authMethod})
          </span>
        </span>
        <div className="flex items-center gap-2">
          <Input
            id={inputId}
            type={show ? "text" : "password"}
            value={value}
            onChange={(e) => onChange(e.target.value)}
            autoComplete="off"
            spellCheck={false}
            data-testid={`package-credential-input-${entry.provider}-${entry.authMethod}`}
          />
          <Button
            type="button"
            size="sm"
            variant="outline"
            onClick={() => setShow((s) => !s)}
            aria-label={show ? `Hide ${entry.credentialEnvVar}` : `Show ${entry.credentialEnvVar}`}
            aria-pressed={show}
          >
            {show ? (
              <>
                <EyeOff className="mr-1 h-3.5 w-3.5" aria-hidden /> Hide
              </>
            ) : (
              <>
                <Eye className="mr-1 h-3.5 w-3.5" aria-hidden /> Show
              </>
            )}
          </Button>
        </div>
      </label>
    </div>
  );
}

function entryKey(entry: PackageRequiredCredentialEntryResponse): string {
  return `${entry.provider}:${entry.authMethod}`;
}

/**
 * Translate the parent's `values` map into the wire-shaped credential
 * payload accepted by `installPackages`. Empty / blank values are
 * dropped so the install request stays tight (the reactive retry form
 * handles whatever the server still flags after Install).
 */
export function buildCredentialPayloadFromValues(
  required: readonly PackageRequiredCredentialEntryResponse[],
  values: Record<string, string>,
): Array<{ provider: string; authMethod: string; value: string }> {
  const out: Array<{ provider: string; authMethod: string; value: string }> = [];
  for (const entry of required) {
    const raw = values[entryKey(entry)];
    if (raw === undefined) continue;
    const trimmed = raw.trim();
    if (trimmed.length === 0) continue;
    out.push({
      provider: entry.provider,
      authMethod: entry.authMethod,
      value: trimmed,
    });
  }
  return out;
}
