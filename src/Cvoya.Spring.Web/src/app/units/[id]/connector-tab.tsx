"use client";

import { useCallback, useEffect, useMemo, useState } from "react";
import { Github, Loader2, RefreshCw } from "lucide-react";

import { Badge } from "@/components/ui/badge";
import { Button } from "@/components/ui/button";
import {
  Card,
  CardContent,
  CardHeader,
  CardTitle,
} from "@/components/ui/card";
import { Input } from "@/components/ui/input";
import { Skeleton } from "@/components/ui/skeleton";
import { useToast } from "@/components/ui/toast";
import { api } from "@/lib/api/client";
import type {
  GitHubInstallationResponse,
  UnitConnectorResponse,
} from "@/lib/api/types";

// Keep in sync with the server's DefaultGitHubEvents (UnitEndpoints.cs) and
// GitHubWebhookRegistrar.SubscribedEvents — the list the Connector tab offers
// for subscription. Server defaults still apply when Events is left empty on
// the wire.
const AVAILABLE_EVENTS: readonly string[] = [
  "issues",
  "pull_request",
  "issue_comment",
  "push",
  "release",
];

interface ConnectorTabProps {
  unitId: string;
}

export function ConnectorTab({ unitId }: ConnectorTabProps) {
  const { toast } = useToast();
  const [config, setConfig] = useState<UnitConnectorResponse | null>(null);
  const [loading, setLoading] = useState(true);
  const [loadError, setLoadError] = useState<string | null>(null);

  const [owner, setOwner] = useState("");
  const [name, setName] = useState("");
  const [installationId, setInstallationId] = useState<number | null>(null);
  const [events, setEvents] = useState<string[]>([]);
  const [saving, setSaving] = useState(false);
  const [saveError, setSaveError] = useState<string | null>(null);

  const [installations, setInstallations] = useState<
    GitHubInstallationResponse[] | null
  >(null);
  const [installationsError, setInstallationsError] = useState<string | null>(
    null,
  );
  const [installUrl, setInstallUrl] = useState<string | null>(null);

  const applyConfig = useCallback((c: UnitConnectorResponse) => {
    setConfig(c);
    setOwner(c.repo?.owner ?? "");
    setName(c.repo?.name ?? "");
    // OpenAPI emits int64 as `number | string` — coerce to number for the
    // local form state. All realistic installation ids fit in JS's
    // MAX_SAFE_INTEGER, so the coercion is lossless in practice.
    setInstallationId(
      c.appInstallationId == null ? null : Number(c.appInstallationId),
    );
    setEvents([...c.events]);
  }, []);

  const loadConfig = useCallback(async () => {
    try {
      const resp = await api.getUnitConnector(unitId);
      applyConfig(resp);
      setLoadError(null);
    } catch (err) {
      const message = err instanceof Error ? err.message : String(err);
      setLoadError(message);
    }
  }, [unitId, applyConfig]);

  const loadInstallations = useCallback(async () => {
    try {
      const list = await api.listGitHubInstallations();
      setInstallations(list);
      setInstallationsError(null);
    } catch (err) {
      const message = err instanceof Error ? err.message : String(err);
      setInstallationsError(message);
      setInstallations([]);
      // Only try to fetch the install URL when the installation list fails —
      // that's the path the "Install App" CTA guards.
      try {
        const { url } = await api.getGitHubInstallUrl();
        setInstallUrl(url);
      } catch {
        // Swallow — the banner text alone is enough when the platform isn't
        // configured for GitHub Apps at all.
      }
    }
  }, []);

  useEffect(() => {
    let cancelled = false;
    setLoading(true);
    Promise.all([loadConfig(), loadInstallations()]).finally(() => {
      if (!cancelled) setLoading(false);
    });
    return () => {
      cancelled = true;
    };
  }, [loadConfig, loadInstallations]);

  const handleSave = async () => {
    setSaveError(null);
    setSaving(true);
    try {
      const resp = await api.setUnitConnector(unitId, {
        type: "github",
        repo: { owner: owner.trim(), name: name.trim() },
        events: events.length > 0 ? events : undefined,
        appInstallationId: installationId ?? undefined,
      });
      applyConfig(resp);
      toast({ title: "Connector saved" });
    } catch (err) {
      const message = err instanceof Error ? err.message : String(err);
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

  const toggleEvent = (e: string) => {
    setEvents((prev) =>
      prev.includes(e) ? prev.filter((x) => x !== e) : [...prev, e],
    );
  };

  const hookBadge = useMemo(() => {
    if (!config) return null;
    if (config.webhookId) {
      return <Badge variant="success">Webhook registered</Badge>;
    }
    if (config.repo) {
      return <Badge variant="outline">No webhook yet</Badge>;
    }
    return <Badge variant="outline">Not configured</Badge>;
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
          <Github className="h-5 w-5" /> GitHub connector
          {hookBadge && <span className="ml-2">{hookBadge}</span>}
        </CardTitle>
      </CardHeader>
      <CardContent className="space-y-5">
        {loadError && (
          <p className="rounded-md border border-destructive/50 bg-destructive/10 px-3 py-2 text-sm text-destructive">
            {loadError}
          </p>
        )}

        {installations && installations.length === 0 && (
          <div className="rounded-md border border-amber-500/50 bg-amber-500/10 px-3 py-2 text-sm text-amber-900 dark:text-amber-200">
            <p className="font-medium">No GitHub App installations found.</p>
            <p className="mt-1">
              Install the app on your account or organisation before configuring
              this unit.
            </p>
            {installUrl && (
              <a
                href={installUrl}
                target="_blank"
                rel="noopener noreferrer"
                className="mt-2 inline-block font-medium underline"
              >
                Install App
              </a>
            )}
            {installationsError && (
              <p className="mt-1 text-xs opacity-80">
                ({installationsError})
              </p>
            )}
          </div>
        )}

        <div className="grid grid-cols-1 gap-4 sm:grid-cols-2">
          <label className="block space-y-1">
            <span className="text-sm text-muted-foreground">
              Repository owner
            </span>
            <Input
              value={owner}
              onChange={(e) => setOwner(e.target.value)}
              placeholder="acme"
            />
          </label>
          <label className="block space-y-1">
            <span className="text-sm text-muted-foreground">
              Repository name
            </span>
            <Input
              value={name}
              onChange={(e) => setName(e.target.value)}
              placeholder="platform"
            />
          </label>
        </div>

        {installations && installations.length > 0 && (
          <div className="space-y-1">
            <span className="text-sm text-muted-foreground">
              App installation
            </span>
            <div className="flex items-center gap-2">
              <select
                className="h-9 flex-1 rounded-md border border-input bg-background px-3 text-sm"
                value={installationId ?? ""}
                onChange={(e) =>
                  setInstallationId(
                    e.target.value === "" ? null : Number(e.target.value),
                  )
                }
              >
                <option value="">(auto — use platform default)</option>
                {installations.map((i) => (
                  <option key={i.installationId} value={i.installationId}>
                    {i.account} ({i.accountType}, {i.repoSelection})
                  </option>
                ))}
              </select>
              <Button
                size="sm"
                variant="outline"
                onClick={loadInstallations}
                aria-label="Refresh installations"
              >
                <RefreshCw className="h-4 w-4" />
              </Button>
            </div>
          </div>
        )}

        <div className="space-y-1">
          <span className="text-sm text-muted-foreground">
            Webhook events
          </span>
          <div className="flex flex-wrap gap-2">
            {AVAILABLE_EVENTS.map((e) => {
              const checked = events.includes(e);
              return (
                <label
                  key={e}
                  className="inline-flex cursor-pointer items-center gap-1 rounded-md border border-border px-2 py-1 text-xs"
                >
                  <input
                    type="checkbox"
                    checked={checked}
                    onChange={() => toggleEvent(e)}
                  />
                  <span>{e}</span>
                </label>
              );
            })}
          </div>
        </div>

        {saveError && (
          <p className="rounded-md border border-destructive/50 bg-destructive/10 px-3 py-2 text-sm text-destructive">
            {saveError}
          </p>
        )}

        <div className="flex justify-end">
          <Button
            onClick={handleSave}
            disabled={saving || !owner.trim() || !name.trim()}
          >
            {saving && <Loader2 className="mr-1 h-4 w-4 animate-spin" />}
            {saving ? "Saving…" : "Save"}
          </Button>
        </div>
      </CardContent>
    </Card>
  );
}
