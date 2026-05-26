"use client";

// Web-search connector wizard step. Mounted inside the create-unit
// wizard before the unit exists. Bubbles a `UnitWebSearchConfigRequest`
// up to the parent wizard, which bundles it into the single
// create-unit call.
//
// Mirrors the same separation the GitHub connector uses
// (see `src/Cvoya.Spring.Connector.GitHub/web/connector-wizard-step.tsx`):
//
// * `connector-tab.tsx`         — mounted on /units/[id] for an
//                                 already-bound unit.
// * `connector-wizard-step.tsx` — mounted inside the create-unit wizard
//                                 before the unit exists. No unit id,
//                                 no live config — it is a pure form
//                                 that produces a config payload.
//
// The host web app resolves this file through the
// `@connector-web-search/*` path alias declared in
// `src/Cvoya.Spring.Web/tsconfig.json`. The component identifier
// (`WebSearchConnectorWizardStep`) is statically registered in
// `src/Cvoya.Spring.Web/src/connectors/registry.ts`.

import { useCallback, useEffect, useMemo, useState } from "react";
import { Loader2, Search } from "lucide-react";

import { api } from "@/lib/api/client";
import { formatTranslatedError } from "@/lib/api/translate-error";
import type {
  UnitWebSearchConfigRequest,
  WebSearchProviderDescriptor,
} from "@/lib/api/types";

const MIN_RESULTS = 1;
const MAX_RESULTS = 50;
const DEFAULT_MAX_RESULTS = 10;

export interface WebSearchConnectorWizardStepProps {
  onChange: (body: UnitWebSearchConfigRequest | null) => void;
  initialValue?: UnitWebSearchConfigRequest | null;
}

/**
 * Wizard-mode web-search connector configuration. Renders the
 * provider picker, the API-key secret-name input, the default
 * result cap, and the safe-search toggle. Bubbles a
 * {@link UnitWebSearchConfigRequest} up to the parent wizard whenever
 * the form holds a valid payload — or `null` while it is incomplete.
 */
export function WebSearchConnectorWizardStep({
  onChange,
  initialValue,
}: WebSearchConnectorWizardStepProps) {
  const [provider, setProvider] = useState(initialValue?.provider ?? "");
  const [apiKeySecretName, setApiKeySecretName] = useState(
    initialValue?.apiKeySecretName ?? "",
  );
  const [maxResults, setMaxResults] = useState<number>(
    initialValue?.maxResults ?? DEFAULT_MAX_RESULTS,
  );
  const [safesearch, setSafesearch] = useState<boolean>(
    initialValue?.safesearch ?? true,
  );

  const [providers, setProviders] = useState<
    WebSearchProviderDescriptor[] | null
  >(null);
  const [providersError, setProvidersError] = useState<string | null>(null);
  const [providersLoading, setProvidersLoading] = useState(true);

  const loadProviders = useCallback(async () => {
    setProvidersLoading(true);
    try {
      const list = await api.listWebSearchProviders();
      setProviders(list);
      setProvidersError(null);
      // First-time render: pre-select the first registered provider so
      // the form is in a saveable state without an extra click.
      setProvider((current) =>
        current !== "" ? current : (list[0]?.id ?? ""),
      );
    } catch (err) {
      setProvidersError(formatTranslatedError(err));
      setProviders([]);
    } finally {
      setProvidersLoading(false);
    }
  }, []);

  useEffect(() => {
    void loadProviders();
  }, [loadProviders]);

  const providerIsRegistered = useMemo(() => {
    if (provider === "") return false;
    if (providers === null) return true;
    return providers.some(
      (p) => p.id.toLowerCase() === provider.toLowerCase(),
    );
  }, [provider, providers]);

  const maxResultsValid =
    Number.isInteger(maxResults) &&
    maxResults >= MIN_RESULTS &&
    maxResults <= MAX_RESULTS;

  useEffect(() => {
    if (provider === "" || !providerIsRegistered || !maxResultsValid) {
      onChange(null);
      return;
    }
    const trimmedSecret = apiKeySecretName.trim();
    onChange({
      provider,
      apiKeySecretName: trimmedSecret === "" ? null : trimmedSecret,
      maxResults,
      safesearch,
    });
  }, [
    provider,
    providerIsRegistered,
    apiKeySecretName,
    maxResults,
    maxResultsValid,
    safesearch,
    onChange,
  ]);

  return (
    <div className="space-y-4 rounded-md border border-border bg-muted/30 p-4">
      <div className="flex items-center gap-2">
        <Search className="h-4 w-4" />
        <span className="text-sm font-medium">Web Search connector</span>
      </div>

      <label className="block space-y-1">
        <span className="text-xs text-muted-foreground">
          Provider<span className="text-destructive"> *</span>
        </span>
        <select
          aria-label="Web search provider"
          data-testid="web-search-provider"
          className="h-9 w-full rounded-md border border-input bg-background px-3 text-sm"
          value={provider}
          onChange={(e) => setProvider(e.target.value)}
          disabled={providersLoading || (providers?.length ?? 0) === 0}
        >
          {providersLoading && (
            <option value="">Loading providers…</option>
          )}
          {provider !== "" && !providerIsRegistered && !providersLoading && (
            <option value={provider}>{provider} (not registered)</option>
          )}
          {providers?.map((p) => (
            <option key={p.id} value={p.id}>
              {p.displayName} ({p.id})
            </option>
          ))}
          {!providersLoading && providers !== null && providers.length === 0 && (
            <option value="">No providers registered</option>
          )}
        </select>
        {providersError && (
          <span className="block text-[11px] text-destructive">
            Could not load providers: {providersError}
          </span>
        )}
        {!providerIsRegistered && provider !== "" && !providersLoading && (
          <span
            className="block text-[11px] text-destructive"
            role="alert"
            data-testid="web-search-provider-validation"
          >
            Pick a registered provider before continuing.
          </span>
        )}
        {providersLoading && (
          <span className="inline-flex items-center gap-1 text-[11px] text-muted-foreground">
            <Loader2 className="h-3 w-3 animate-spin" />
            Fetching the provider catalogue…
          </span>
        )}
      </label>

      <label className="block space-y-1">
        <span className="text-xs text-muted-foreground">
          API-key secret name
        </span>
        <input
          type="text"
          aria-label="API-key secret name"
          data-testid="web-search-api-key-secret-name"
          className="h-9 w-full rounded-md border border-input bg-background px-3 text-sm font-mono"
          placeholder="unit/<unit-id>/web-search/api-key"
          value={apiKeySecretName}
          onChange={(e) => setApiKeySecretName(e.target.value)}
        />
        <span className="block text-[11px] text-muted-foreground">
          Name of a unit-scoped secret whose value is the provider&apos;s
          API key. Resolved at invoke time — plaintext is never stored
          on the binding. Leave empty for providers that do not require
          authentication.
        </span>
      </label>

      <label className="block space-y-1">
        <span className="text-xs text-muted-foreground">Max results</span>
        <input
          type="number"
          aria-label="Max results"
          data-testid="web-search-max-results"
          className="h-9 w-32 rounded-md border border-input bg-background px-3 text-sm font-mono"
          min={MIN_RESULTS}
          max={MAX_RESULTS}
          value={maxResults}
          onChange={(e) => {
            const next = Number.parseInt(e.target.value, 10);
            setMaxResults(Number.isNaN(next) ? DEFAULT_MAX_RESULTS : next);
          }}
        />
        {!maxResultsValid && (
          <span
            className="block text-[11px] text-destructive"
            role="alert"
            data-testid="web-search-max-results-validation"
          >
            Must be an integer between {MIN_RESULTS} and {MAX_RESULTS}.
          </span>
        )}
        <span className="block text-[11px] text-muted-foreground">
          Default cap on the number of results the skill returns. The
          server hard-caps at {MAX_RESULTS}.
        </span>
      </label>

      <label className="inline-flex cursor-pointer items-center gap-2 text-sm">
        <input
          type="checkbox"
          checked={safesearch}
          onChange={(e) => setSafesearch(e.target.checked)}
          data-testid="web-search-safesearch"
        />
        <span className="font-medium text-foreground">
          Enable safe-search filter
        </span>
      </label>
    </div>
  );
}
