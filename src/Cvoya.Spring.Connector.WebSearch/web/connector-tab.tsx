"use client";

// Web-search connector UI (post-bind). Lives inside the connector
// package (`src/Cvoya.Spring.Connector.WebSearch/web/`), mirroring the
// server-side layout where a connector owns both its .NET code AND
// its web surface — the same pattern the GitHub connector uses (see
// `src/Cvoya.Spring.Connector.GitHub/web/connector-tab.tsx`).
//
// The host web app resolves this file through the
// `@connector-web-search/*` path alias declared in
// `src/Cvoya.Spring.Web/tsconfig.json`. The entry is registered in
// `src/Cvoya.Spring.Web/src/connectors/registry.ts` so the unit's
// Connector tab knows to mount this component for `slug: "web-search"`.
//
// The config surface is intentionally small: provider (from a registered
// catalogue), the unit-scoped secret-name reference for the provider's
// API key (never plaintext), the default result cap, and the safe-search
// toggle. Operators who need richer provider catalogues author them
// server-side via `IWebSearchProvider`; the picker is fed by the live
// `ListWebSearchProviders` endpoint so a new provider appears here with
// no UI change.

import { useCallback, useEffect, useMemo, useState } from "react";
import { Loader2, Search } from "lucide-react";

import { Badge } from "@/components/ui/badge";
import { Button } from "@/components/ui/button";
import {
  Card,
  CardContent,
  CardHeader,
  CardTitle,
} from "@/components/ui/card";
import { Skeleton } from "@/components/ui/skeleton";
import { useToast } from "@/components/ui/toast";
import { api } from "@/lib/api/client";
import { formatTranslatedError } from "@/lib/api/translate-error";
import type {
  UnitWebSearchConfigResponse,
  WebSearchProviderDescriptor,
} from "@/lib/api/types";

const MIN_RESULTS = 1;
const MAX_RESULTS = 50;
const DEFAULT_MAX_RESULTS = 10;

export interface WebSearchConnectorTabProps {
  unitId: string;
}

export function WebSearchConnectorTab({ unitId }: WebSearchConnectorTabProps) {
  const { toast } = useToast();
  const [config, setConfig] = useState<UnitWebSearchConfigResponse | null>(
    null,
  );
  const [providers, setProviders] = useState<
    WebSearchProviderDescriptor[] | null
  >(null);
  const [loading, setLoading] = useState(true);
  const [loadError, setLoadError] = useState<string | null>(null);
  const [providersError, setProvidersError] = useState<string | null>(null);
  const [provider, setProvider] = useState("");
  const [apiKeySecretName, setApiKeySecretName] = useState("");
  const [maxResults, setMaxResults] = useState<number>(DEFAULT_MAX_RESULTS);
  const [safesearch, setSafesearch] = useState<boolean>(true);
  const [saving, setSaving] = useState(false);
  const [saveError, setSaveError] = useState<string | null>(null);

  const applyConfig = useCallback((c: UnitWebSearchConfigResponse) => {
    setConfig(c);
    setProvider(c.provider);
    setApiKeySecretName(c.apiKeySecretName ?? "");
    setMaxResults(c.maxResults);
    setSafesearch(c.safesearch);
  }, []);

  const loadConfig = useCallback(async () => {
    try {
      const resp = await api.getUnitWebSearchConfig(unitId);
      if (resp) {
        applyConfig(resp);
      }
      setLoadError(null);
    } catch (err) {
      setLoadError(formatTranslatedError(err));
    }
  }, [unitId, applyConfig]);

  const loadProviders = useCallback(async () => {
    try {
      const list = await api.listWebSearchProviders();
      setProviders(list);
      setProvidersError(null);
      // When the binding hasn't been read yet (fresh bind, no config row),
      // seed the picker with the first registered provider so the form
      // never starts in an unsaveable empty state.
      setProvider((current) =>
        current !== "" ? current : (list[0]?.id ?? ""),
      );
    } catch (err) {
      setProvidersError(formatTranslatedError(err));
      setProviders([]);
    }
  }, []);

  useEffect(() => {
    let cancelled = false;
    setLoading(true);
    Promise.all([loadConfig(), loadProviders()]).finally(() => {
      if (!cancelled) setLoading(false);
    });
    return () => {
      cancelled = true;
    };
  }, [loadConfig, loadProviders]);

  const providerIsRegistered = useMemo(() => {
    if (provider === "") return false;
    if (providers === null) return true; // benefit of doubt while loading
    return providers.some(
      (p) => p.id.toLowerCase() === provider.toLowerCase(),
    );
  }, [provider, providers]);

  const maxResultsValid =
    Number.isInteger(maxResults) &&
    maxResults >= MIN_RESULTS &&
    maxResults <= MAX_RESULTS;

  const formValid =
    provider !== "" && providerIsRegistered && maxResultsValid;

  const handleSave = async () => {
    setSaveError(null);
    setSaving(true);
    try {
      const trimmedSecret = apiKeySecretName.trim();
      const resp = await api.putUnitWebSearchConfig(unitId, {
        provider,
        apiKeySecretName: trimmedSecret === "" ? null : trimmedSecret,
        maxResults,
        safesearch,
      });
      applyConfig(resp);
      toast({ title: "Connector saved" });
    } catch (err) {
      const message = formatTranslatedError(err);
      setSaveError(message);
      toast({
        title: "Failed to save connector",
        description: message,
        variant: "destructive",
      });
    } finally {
      setSaving(false);
    }
  };

  const statusBadge = useMemo(() => {
    if (!config) {
      return <Badge variant="outline">Not configured</Badge>;
    }
    return <Badge variant="outline">{config.provider}</Badge>;
  }, [config]);

  if (loading) {
    return (
      <Card>
        <CardContent className="space-y-3 p-6">
          <Skeleton className="h-4 w-40" />
          <Skeleton className="h-10" />
          <Skeleton className="h-10" />
        </CardContent>
      </Card>
    );
  }

  return (
    <Card>
      <CardHeader>
        <CardTitle className="flex items-center gap-2">
          <Search className="h-5 w-5" /> Web Search connector
          <span className="ml-2">{statusBadge}</span>
        </CardTitle>
      </CardHeader>
      <CardContent className="space-y-5">
        {loadError && (
          <p className="rounded-md border border-destructive/50 bg-destructive/10 px-3 py-2 text-sm text-destructive">
            {loadError}
          </p>
        )}

        <label className="block space-y-1">
          <span className="text-sm text-muted-foreground">Provider</span>
          <select
            aria-label="Web search provider"
            data-testid="web-search-provider"
            className="h-9 w-full rounded-md border border-input bg-background px-3 text-sm"
            value={provider}
            onChange={(e) => setProvider(e.target.value)}
            disabled={providers === null || providers.length === 0}
          >
            {provider !== "" && !providerIsRegistered && (
              <option value={provider}>{provider} (not registered)</option>
            )}
            {providers?.map((p) => (
              <option key={p.id} value={p.id}>
                {p.displayName} ({p.id})
              </option>
            ))}
            {providers !== null && providers.length === 0 && (
              <option value="">No providers registered</option>
            )}
          </select>
          {providersError && (
            <span className="block text-[11px] text-destructive">
              Could not load providers: {providersError}
            </span>
          )}
          {!providerIsRegistered && provider !== "" && (
            <span
              className="block text-[11px] text-destructive"
              role="alert"
              data-testid="web-search-provider-validation"
            >
              The stored provider is not registered on this host. Pick a
              registered provider or ask an operator to enable it.
            </span>
          )}
        </label>

        <label className="block space-y-1">
          <span className="text-sm text-muted-foreground">
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
            API key. The skill resolves it at invoke time — plaintext is
            never stored on the binding. Leave empty for providers that
            do not require authentication (e.g. a self-hosted SearxNG).
          </span>
        </label>

        <label className="block space-y-1">
          <span className="text-sm text-muted-foreground">Max results</span>
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

        {saveError && (
          <p className="rounded-md border border-destructive/50 bg-destructive/10 px-3 py-2 text-sm text-destructive">
            {saveError}
          </p>
        )}

        <div className="flex justify-end">
          <Button onClick={handleSave} disabled={saving || !formValid}>
            {saving && <Loader2 className="mr-1 h-4 w-4 animate-spin" />}
            {saving ? "Saving…" : "Save"}
          </Button>
        </div>
      </CardContent>
    </Card>
  );
}
