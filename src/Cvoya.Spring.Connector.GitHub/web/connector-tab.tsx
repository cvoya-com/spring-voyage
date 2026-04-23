"use client";

// GitHub connector UI. This file lives inside the connector package
// (`src/Cvoya.Spring.Connector.GitHub/web/`), mirroring the server-side
// layout where a connector owns both its .NET code AND its web surface.
//
// Turbopack resolves `node_modules` from this out-of-tree location because
// `src/Cvoya.Spring.Web/next.config.ts` sets `turbopack.root` to the
// repository root — see that file for the rationale. The web project
// imports this component through the `@connector-github/*` path alias
// declared in `src/Cvoya.Spring.Web/tsconfig.json`.

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
import { ApiError, api } from "@/lib/api/client";
import type {
  GitHubInstallationResponse,
  UnitGitHubConfigResponse,
} from "@/lib/api/types";

// Mirror of the helpers used by the wizard step (see
// connector-wizard-step.tsx). Duplicated rather than shared because the
// connector package is consumed via path alias and the two surfaces are
// deliberately independent — we don't want a shared helper to drag the
// post-bind tab into the wizard's bundle, or vice versa.
const GITHUB_APP_DOCS_URL =
  "https://github.com/cvoya-com/spring-voyage/blob/main/docs/guide/deployment.md#optional--connector-credentials";

function extractDisabledReason(err: unknown): string | null {
  if (!(err instanceof ApiError) || err.status !== 404) {
    return null;
  }
  const body = err.body as { disabled?: unknown; reason?: unknown } | null;
  if (
    body !== null &&
    typeof body === "object" &&
    body.disabled === true &&
    typeof body.reason === "string"
  ) {
    return body.reason;
  }
  return null;
}

// Keep in sync with the server's DefaultGitHubEvents (GitHubConnectorType.cs)
// and GitHubWebhookRegistrar.SubscribedEvents. Server defaults still apply
// when the Events field is left empty on the wire.
const AVAILABLE_EVENTS: readonly string[] = [
  "issues",
  "pull_request",
  "issue_comment",
  "push",
  "release",
];

export interface GitHubConnectorTabProps {
  unitId: string;
}

export function GitHubConnectorTab({ unitId }: GitHubConnectorTabProps) {
  const { toast } = useToast();
  const [config, setConfig] = useState<UnitGitHubConfigResponse | null>(null);
  const [loading, setLoading] = useState(true);
  const [loadError, setLoadError] = useState<string | null>(null);

  const [owner, setOwner] = useState("");
  const [repo, setRepo] = useState("");
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
  // disabled-with-reason is a first-class connector state distinct from
  // a network error or an unconfigured repo (#1186). When set we hide the
  // install affordances and render a remediation panel instead.
  const [disabledReason, setDisabledReason] = useState<string | null>(null);

  const applyConfig = useCallback((c: UnitGitHubConfigResponse) => {
    setConfig(c);
    setOwner(c.owner);
    setRepo(c.repo);
    // OpenAPI emits int64 as `number | string` — coerce to number for the
    // local form state. All realistic installation ids fit in
    // MAX_SAFE_INTEGER, so the coercion is lossless in practice.
    setInstallationId(
      c.appInstallationId == null ? null : Number(c.appInstallationId),
    );
    setEvents([...c.events]);
  }, []);

  const loadConfig = useCallback(async () => {
    try {
      const resp = await api.getUnitGitHubConfig(unitId);
      if (resp) {
        applyConfig(resp);
      }
      setLoadError(null);
    } catch (err) {
      const message = err instanceof Error ? err.message : String(err);
      setLoadError(message);
    }
  }, [unitId, applyConfig]);

  const loadInstallations = useCallback(async () => {
    let list: GitHubInstallationResponse[] = [];
    let disabled: string | null = null;
    try {
      list = await api.listGitHubInstallations();
      setInstallations(list);
      setInstallationsError(null);
      setDisabledReason(null);
    } catch (err) {
      disabled = extractDisabledReason(err);
      if (disabled !== null) {
        setDisabledReason(disabled);
        setInstallationsError(null);
      } else {
        const message = err instanceof Error ? err.message : String(err);
        setInstallationsError(message);
        setDisabledReason(null);
      }
      setInstallations([]);
    }
    // Fetch the install URL whenever the empty-state banner will show
    // (either the list came back empty, or the call errored). Keeps the
    // post-bind surface in parity with the create-unit wizard (#599).
    // Skip when the connector is disabled at the deployment level — the
    // install URL endpoint will return the same 404 with no URL to show.
    if (disabled !== null) {
      return;
    }
    if (list.length === 0) {
      try {
        const { url } = await api.getGitHubInstallUrl();
        setInstallUrl(url);
      } catch {
        // Swallow — banner text alone is enough when the platform isn't
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
      const resp = await api.putUnitGitHubConfig(unitId, {
        owner: owner.trim(),
        repo: repo.trim(),
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

  const statusBadge = useMemo(() => {
    if (!config) return <Badge variant="outline">Not configured</Badge>;
    return <Badge variant="outline">{`${config.owner}/${config.repo}`}</Badge>;
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
          <span className="ml-2">{statusBadge}</span>
        </CardTitle>
      </CardHeader>
      <CardContent className="space-y-5">
        {loadError && (
          <p className="rounded-md border border-destructive/50 bg-destructive/10 px-3 py-2 text-sm text-destructive">
            {loadError}
          </p>
        )}

        {disabledReason !== null && (
          <div
            role="alert"
            className="rounded-md border border-info/50 bg-info/15 px-3 py-2 text-sm text-info"
          >
            <p className="font-medium">
              GitHub connector not configured on this deployment.
            </p>
            <p className="mt-1 text-foreground">{disabledReason}</p>
            <p className="mt-2 text-xs text-foreground">
              An operator needs to register a GitHub App and set
              <code className="mx-1 rounded bg-muted px-1 py-0.5 text-[11px]">
                GitHub__AppId
              </code>
              /
              <code className="mx-1 rounded bg-muted px-1 py-0.5 text-[11px]">
                GitHub__PrivateKeyPem
              </code>
              /
              <code className="mx-1 rounded bg-muted px-1 py-0.5 text-[11px]">
                GitHub__WebhookSecret
              </code>
              in <code className="rounded bg-muted px-1 py-0.5 text-[11px]">
                spring.env
              </code>{" "}
              before this unit can deliver events.
            </p>
            <a
              href={GITHUB_APP_DOCS_URL}
              target="_blank"
              rel="noopener noreferrer"
              className="mt-2 inline-flex h-8 items-center gap-1 rounded-md border border-info/60 bg-info/10 px-3 text-sm font-medium text-info transition-colors hover:bg-info/20 focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring focus-visible:ring-offset-2 focus-visible:ring-offset-background"
            >
              View deployment guide
            </a>
          </div>
        )}

        {disabledReason === null &&
          installations &&
          installations.length === 0 && (
            <div
              role="alert"
              className="rounded-md border border-warning/50 bg-warning/15 px-3 py-2 text-sm text-warning"
            >
              <p className="font-medium">No GitHub App installations found.</p>
              <p className="mt-1 text-foreground">
                Install the app on your account or organisation before
                configuring this unit.
              </p>
              {installUrl && (
                <a
                  href={installUrl}
                  target="_blank"
                  rel="noopener noreferrer"
                  className="mt-2 inline-flex h-8 items-center gap-1 rounded-md border border-warning/60 bg-warning/10 px-3 text-sm font-medium text-warning transition-colors hover:bg-warning/20 focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring focus-visible:ring-offset-2 focus-visible:ring-offset-background"
                >
                  <Github className="h-4 w-4" aria-hidden="true" />
                  Install GitHub App
                </a>
              )}
              {installationsError && (
                <p className="mt-2 text-xs text-muted-foreground">
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
              value={repo}
              onChange={(e) => setRepo(e.target.value)}
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
          <span className="text-sm text-muted-foreground">Webhook events</span>
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
            disabled={saving || !owner.trim() || !repo.trim()}
          >
            {saving && <Loader2 className="mr-1 h-4 w-4 animate-spin" />}
            {saving ? "Saving…" : "Save"}
          </Button>
        </div>
      </CardContent>
    </Card>
  );
}
