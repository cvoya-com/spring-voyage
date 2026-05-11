"use client";

import { useCallback, useState } from "react";
import { AlertTriangle, Eye, EyeOff, KeyRound, RefreshCw } from "lucide-react";

import { Button } from "@/components/ui/button";
import { Input } from "@/components/ui/input";
import { translateApiError } from "@/lib/api/translate-error";
import type { CredentialBindingPayload } from "@/lib/api/types";
import type { MissingCredentialEntry } from "@/lib/api/translate-error";

/**
 * #2169: inline retry form for the install wizard's `CredentialsMissing`
 * pre-flight rejection. Mirrors the CLI's `TryPromptForMissingCredentialsAsync`
 * (`Cvoya.Spring.Cli/Commands/PackageCommand.cs`) — one input per
 * `(provider, authMethod)` entry the server flagged as missing, plus a
 * single "Retry install" button that re-fires the install with the
 * supplied values attached as `PackageInstallTarget.credentials[]`. The
 * server writes accepted values as tenant secrets (idempotent rotate
 * on re-supply) before Phase 1 runs.
 *
 * Inputs are uncontrolled-from-the-form's-perspective: the parent owns
 * the `values` map (keyed by `${provider}:${authMethod}`) and gets a
 * fresh map back on every keystroke. Surviving across retry attempts
 * is the parent's responsibility — the server may re-reject with a
 * shorter `missing[]` (e.g. one of the values was empty), and we
 * preserve whatever the operator already typed for the still-missing
 * entries.
 */
export function CredentialsMissingRetryForm({
  error,
  missing,
  values,
  onValuesChange,
  onRetry,
  submitting,
}: {
  error: unknown;
  missing: MissingCredentialEntry[];
  values: Record<string, string>;
  onValuesChange: (values: Record<string, string>) => void;
  onRetry: (credentials: CredentialBindingPayload[]) => void;
  submitting: boolean;
}) {
  const translated = translateApiError(error);

  const setValue = useCallback(
    (key: string, value: string) => {
      onValuesChange({ ...values, [key]: value });
    },
    [onValuesChange, values],
  );

  const buildPayload = (): CredentialBindingPayload[] => {
    const out: CredentialBindingPayload[] = [];
    for (const entry of missing) {
      const key = entryKey(entry);
      const raw = values[key];
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
  };

  const handleSubmit = (event: React.FormEvent<HTMLFormElement>) => {
    event.preventDefault();
    if (submitting) return;
    const payload = buildPayload();
    if (payload.length === 0) {
      // Mirror the CLI: don't fire a no-op retry; the server would
      // re-reject with the same error and the operator gets stuck.
      return;
    }
    onRetry(payload);
  };

  const filledCount = missing.reduce((acc, entry) => {
    const v = values[entryKey(entry)];
    return acc + (v !== undefined && v.trim().length > 0 ? 1 : 0);
  }, 0);
  const canRetry = !submitting && filledCount > 0;

  return (
    <form
      role="alert"
      onSubmit={handleSubmit}
      data-testid="credentials-missing-retry-form"
      className="space-y-3 rounded-md border border-destructive/50 bg-destructive/10 px-3 py-3 text-sm text-foreground"
    >
      <div className="flex items-start gap-2">
        <AlertTriangle
          className="mt-0.5 h-4 w-4 shrink-0 text-destructive"
          aria-hidden="true"
        />
        <div className="min-w-0 flex-1">
          <p className="font-medium text-destructive">{translated.title}</p>
          <p className="mt-1 text-destructive/90">
            Paste each value below; the install retries with these
            credentials and writes them as tenant secrets so this does
            not block again next time.
          </p>
        </div>
      </div>

      <ul className="space-y-3">
        {missing.map((entry) => (
          <li key={entryKey(entry)}>
            <CredentialField
              entry={entry}
              value={values[entryKey(entry)] ?? ""}
              onChange={(v) => setValue(entryKey(entry), v)}
              disabled={submitting}
            />
          </li>
        ))}
      </ul>

      <div className="flex flex-wrap items-center gap-2">
        <Button
          type="submit"
          size="sm"
          variant="default"
          disabled={!canRetry}
          data-testid="credentials-missing-retry-button"
        >
          <RefreshCw className="mr-1.5 h-3.5 w-3.5" aria-hidden />
          {submitting ? "Retrying…" : "Retry install"}
        </Button>
        {filledCount === 0 && !submitting && (
          <span className="text-xs text-muted-foreground">
            Enter at least one value to retry.
          </span>
        )}
      </div>
    </form>
  );
}

function entryKey(entry: MissingCredentialEntry): string {
  return `${entry.provider}:${entry.authMethod}`;
}

function CredentialField({
  entry,
  value,
  onChange,
  disabled,
}: {
  entry: MissingCredentialEntry;
  value: string;
  onChange: (value: string) => void;
  disabled: boolean;
}) {
  const [show, setShow] = useState(false);
  const inputId = `credentials-missing-input-${entry.provider}-${entry.authMethod}`;
  const label =
    entry.credentialEnvVar ??
    entry.secretName ??
    `${entry.provider} / ${entry.authMethod}`;

  return (
    <label htmlFor={inputId} className="block space-y-1">
      <span className="flex items-center gap-1.5 text-xs font-medium text-foreground">
        <KeyRound className="h-3 w-3 text-muted-foreground" aria-hidden />
        <code className="rounded bg-muted px-1 py-0.5 font-mono text-[11px] text-foreground">
          {label}
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
          disabled={disabled}
          autoComplete="off"
          spellCheck={false}
          data-testid={`credentials-missing-input-${entry.provider}-${entry.authMethod}`}
        />
        <Button
          type="button"
          size="sm"
          variant="outline"
          onClick={() => setShow((s) => !s)}
          aria-label={show ? `Hide ${label}` : `Show ${label}`}
          aria-pressed={show}
          disabled={disabled}
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
  );
}
