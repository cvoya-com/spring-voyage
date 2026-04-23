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
//
// #1133: the post-bind tab tracks the wizard's UX rewrite — Repository and
// Reviewer dropdowns sourced from the aggregated `/list-repositories` and
// per-repo `/list-collaborators` endpoints. The owner / repo / installation
// fields are no longer typed by hand.

import { useCallback, useEffect, useMemo, useState } from "react";
import { Github, Loader2, Lock, RefreshCw } from "lucide-react";

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
import { ApiError, api } from "@/lib/api/client";
import type {
  GitHubCollaboratorResponse,
  GitHubRepositoryResponse,
  UnitGitHubConfigResponse,
} from "@/lib/api/types";

// Mirror of the helpers used by the wizard step (see
// connector-wizard-step.tsx). Duplicated rather than shared because the
// connector package is consumed via path alias and the two surfaces are
// deliberately independent — we don't want a shared helper to drag the
// post-bind tab into the wizard's bundle, or vice versa.
const GITHUB_APP_DOCS_URL =
  "https://github.com/cvoya-com/spring-voyage/blob/main/docs/guide/deployment.md#optional--connector-credentials";

const NO_REVIEWER = "";

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

// Mirror of `GitHubConnectorType.DefaultEvents`. When the operator
// re-enables "Connector defaults" we collapse the local explicit set
// back to this list — both for the wire shape (we send `events:
// undefined`, but the local checkbox state still needs to reflect the
// row the user will see if they uncheck again) and for the
// pre-population behaviour the wizard ships under #1127. Kept
// duplicated alongside the wizard's copy on purpose — see the comment
// in `connector-wizard-step.tsx`.
const DEFAULT_EVENTS: readonly string[] = [
  "issues",
  "pull_request",
  "issue_comment",
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
  const [reviewer, setReviewer] = useState("");
  // #1146: matches the wizard's split (#1127) — `useDefaults` is the
  // primary control, `events` carries the local explicit selection
  // that the per-event row reflects when the toggle is unchecked.
  // Initialized to []/true so the very first paint (before the load
  // resolves) shows the disabled informational row rather than an
  // empty enabled one. `applyConfig` rehydrates both fields from the
  // server's `eventsAreDefault` flag the moment the GET resolves.
  const [useDefaults, setUseDefaults] = useState<boolean>(true);
  const [events, setEvents] = useState<string[]>([]);
  const [saving, setSaving] = useState(false);
  const [saveError, setSaveError] = useState<string | null>(null);

  const [repositories, setRepositories] = useState<
    GitHubRepositoryResponse[] | null
  >(null);
  const [reposError, setReposError] = useState<string | null>(null);
  const [reposLoading, setReposLoading] = useState(false);

  const [collaborators, setCollaborators] = useState<
    GitHubCollaboratorResponse[] | null
  >(null);
  const [collaboratorsLoading, setCollaboratorsLoading] = useState(false);
  const [collaboratorsError, setCollaboratorsError] = useState<string | null>(
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
    setReviewer(c.reviewer ?? "");
    // #1146: the server's `eventsAreDefault` flag is authoritative for
    // the toggle's initial state. When true, the response's `events`
    // field is the connector's defaults materialized server-side; we
    // seed the local `events` state from it anyway so the moment the
    // operator unchecks the toggle they see the same row of marks they
    // were already living with (matches the wizard's behaviour from
    // #1127). When false, `events` is the operator's explicit set and
    // we surface it verbatim.
    setUseDefaults(c.eventsAreDefault);
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

  // #1132: `reposLoading` doubles as the in-flight indicator for the
  // Recheck button — operators editing an existing unit get the same
  // affordance as operators using the create-unit wizard.
  const loadRepositories = useCallback(async () => {
    let list: GitHubRepositoryResponse[] = [];
    let disabled: string | null = null;
    setReposLoading(true);
    try {
      list = await api.listGitHubRepositories();
      setRepositories(list);
      setReposError(null);
      setDisabledReason(null);
    } catch (err) {
      disabled = extractDisabledReason(err);
      if (disabled !== null) {
        setDisabledReason(disabled);
        setReposError(null);
      } else {
        const message = err instanceof Error ? err.message : String(err);
        setReposError(message);
        setDisabledReason(null);
      }
      setRepositories([]);
    } finally {
      setReposLoading(false);
    }
    // Fetch the install URL whenever the empty-state banner will show
    // (either the list came back empty, or the call errored). Keeps the
    // post-bind surface in parity with the create-unit wizard (#599).
    // Skip when the connector is disabled at the deployment level — the
    // install URL endpoint will return the same 404 with no URL to show.
    if (disabled === null && list.length === 0) {
      try {
        const { url } = await api.getGitHubInstallUrl();
        setInstallUrl(url);
      } catch {
        // Swallow — banner text alone is enough when the platform isn't
        // configured for GitHub Apps at all.
      }
    }
  }, []);

  // Re-fetch collaborators whenever the chosen repo changes — same
  // behaviour as the wizard step.
  useEffect(() => {
    if (
      installationId == null ||
      owner.trim() === "" ||
      repo.trim() === ""
    ) {
      setCollaborators(null);
      setCollaboratorsError(null);
      setCollaboratorsLoading(false);
      return;
    }
    let cancelled = false;
    setCollaboratorsLoading(true);
    (async () => {
      try {
        const list = await api.listGitHubCollaborators(
          installationId,
          owner,
          repo,
        );
        if (cancelled) return;
        setCollaborators(list);
        setCollaboratorsError(null);
      } catch (err) {
        if (cancelled) return;
        const message = err instanceof Error ? err.message : String(err);
        setCollaborators([]);
        setCollaboratorsError(message);
      } finally {
        if (!cancelled) setCollaboratorsLoading(false);
      }
    })();
    return () => {
      cancelled = true;
    };
  }, [installationId, owner, repo]);

  useEffect(() => {
    let cancelled = false;
    setLoading(true);
    Promise.all([loadConfig(), loadRepositories()]).finally(() => {
      if (!cancelled) setLoading(false);
    });
    return () => {
      cancelled = true;
    };
  }, [loadConfig, loadRepositories]);

  const handleSave = async () => {
    setSaveError(null);
    setSaving(true);
    try {
      const resp = await api.putUnitGitHubConfig(unitId, {
        owner: owner.trim(),
        repo: repo.trim(),
        // #1146 / parity with the wizard (#1127): omit `events` when
        // the operator picked "Connector defaults" so the server
        // resolves the set itself; forward the explicit list verbatim
        // otherwise. The server's PutConfig already collapses an empty
        // list to null, but bubbling the explicit selection is the
        // user's intent of record either way.
        events: useDefaults
          ? undefined
          : events.length > 0
            ? events
            : undefined,
        appInstallationId: installationId ?? undefined,
        reviewer: reviewer.trim() === "" ? undefined : reviewer.trim(),
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
    if (!config || !config.owner || !config.repo) {
      return <Badge variant="outline">Not configured</Badge>;
    }
    return <Badge variant="outline">{`${config.owner}/${config.repo}`}</Badge>;
  }, [config]);

  const selectedFullName =
    owner !== "" && repo !== "" ? `${owner}/${repo}` : "";

  // Persisted owner/repo may not be present in the live repository list
  // (e.g. the App lost access). Synthesise a placeholder row so the
  // dropdown still shows the current binding rather than collapsing to
  // "Select…", which would silently invite the user to drop the binding.
  const repoOptions = useMemo(() => {
    const base = repositories ?? [];
    if (
      selectedFullName !== "" &&
      installationId != null &&
      !base.some((r) => r.fullName === selectedFullName)
    ) {
      return [
        ...base,
        {
          installationId,
          repositoryId: 0,
          owner,
          repo,
          fullName: selectedFullName,
          private: false,
        } as GitHubRepositoryResponse,
      ];
    }
    return base;
  }, [repositories, selectedFullName, installationId, owner, repo]);

  const handleRepoChange = (next: string) => {
    if (next === "") {
      setOwner("");
      setRepo("");
      setInstallationId(null);
      setReviewer("");
      return;
    }
    const match = repoOptions.find((r) => r.fullName === next) ?? null;
    if (match === null) return;
    setOwner(match.owner);
    setRepo(match.repo);
    setInstallationId(Number(match.installationId));
    setReviewer("");
  };

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
          repositories &&
          repositories.length === 0 && (
            <div
              role="alert"
              className="rounded-md border border-warning/50 bg-warning/15 px-3 py-2 text-sm text-warning"
            >
              <p className="font-medium">No GitHub repositories visible.</p>
              <p className="mt-1 text-foreground">
                Install the app on your account or organisation, and grant it
                access to at least one repository, before configuring this
                unit.
              </p>
              {/* #1132: parity with the create-unit wizard step. After
                  the operator installs the App on github.com they need
                  to come back here and tell the panel to re-check —
                  without this the panel was stuck on "No installations"
                  and the operator had to refresh the whole page. The
                  button is omitted (along with the install link) when
                  the connector is disabled at the deployment level —
                  there are no credentials to check yet. */}
              <div className="mt-2 flex flex-wrap items-center gap-2">
                {installUrl && (
                  <a
                    href={installUrl}
                    target="_blank"
                    rel="noopener noreferrer"
                    className="inline-flex h-8 items-center gap-1 rounded-md border border-warning/60 bg-warning/10 px-3 text-sm font-medium text-warning transition-colors hover:bg-warning/20 focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring focus-visible:ring-offset-2 focus-visible:ring-offset-background"
                  >
                    <Github className="h-4 w-4" aria-hidden="true" />
                    Install GitHub App
                  </a>
                )}
                <Button
                  size="sm"
                  variant="outline"
                  onClick={loadRepositories}
                  disabled={reposLoading}
                  aria-label="Recheck installations"
                  aria-busy={reposLoading}
                  data-testid="github-recheck-installations"
                >
                  {reposLoading ? (
                    <Loader2
                      className="mr-1 h-4 w-4 animate-spin"
                      aria-hidden="true"
                    />
                  ) : (
                    <RefreshCw
                      className="mr-1 h-4 w-4"
                      aria-hidden="true"
                    />
                  )}
                  {reposLoading ? "Rechecking…" : "Recheck installations"}
                  {reposLoading && (
                    <span className="sr-only">
                      Refreshing GitHub App installations
                    </span>
                  )}
                </Button>
              </div>
              {reposError && (
                <p className="mt-2 text-xs text-muted-foreground">
                  ({reposError})
                </p>
              )}
            </div>
          )}

        {disabledReason === null && (
          <label className="block space-y-1">
            <span className="text-sm text-muted-foreground">Repository</span>
            <div className="flex items-center gap-2">
              <select
                aria-label="Repository"
                className="h-9 flex-1 rounded-md border border-input bg-background px-3 text-sm"
                value={selectedFullName}
                onChange={(e) => handleRepoChange(e.target.value)}
                disabled={reposLoading || repoOptions.length === 0}
              >
                <option value="">
                  {reposLoading
                    ? "Loading repositories…"
                    : repoOptions.length === 0
                      ? "No repositories available"
                      : "Select a repository…"}
                </option>
                {repoOptions.map((r) => (
                  <option
                    key={`${r.installationId}:${r.repositoryId}:${r.fullName}`}
                    value={r.fullName}
                  >
                    {r.fullName}
                    {r.private ? " (private)" : ""}
                  </option>
                ))}
              </select>
              <Button
                size="sm"
                variant="outline"
                onClick={loadRepositories}
                aria-label="Refresh repositories"
                aria-busy={reposLoading}
                disabled={reposLoading}
              >
                {reposLoading ? (
                  <Loader2 className="h-4 w-4 animate-spin" />
                ) : (
                  <RefreshCw className="h-4 w-4" />
                )}
              </Button>
            </div>
            {selectedFullName !== "" &&
              repoOptions.find((r) => r.fullName === selectedFullName)
                ?.private && (
                <span className="inline-flex items-center gap-1 text-[11px] text-muted-foreground">
                  <Lock className="h-3 w-3" aria-hidden="true" />
                  Private repository.
                </span>
              )}
          </label>
        )}

        {disabledReason === null && installationId != null && (
          <label className="block space-y-1">
            <span className="text-sm text-muted-foreground">
              Default reviewer
            </span>
            <select
              aria-label="Default reviewer"
              className="h-9 w-full rounded-md border border-input bg-background px-3 text-sm"
              value={reviewer}
              onChange={(e) => setReviewer(e.target.value)}
              disabled={collaboratorsLoading}
            >
              <option value={NO_REVIEWER}>
                {collaboratorsLoading
                  ? "Loading collaborators…"
                  : "(none — agents pick per call)"}
              </option>
              {/* If the persisted reviewer isn't (yet) in the loaded list,
                  surface it anyway so the current value remains visible
                  while the live collaborator list catches up (or stays
                  unreachable). */}
              {reviewer !== "" &&
                !collaborators?.some((c) => c.login === reviewer) && (
                  <option value={reviewer}>{reviewer}</option>
                )}
              {collaborators?.map((c) => (
                <option key={c.login} value={c.login}>
                  {c.login}
                </option>
              ))}
            </select>
            {collaboratorsError && (
              <span className="block text-[11px] text-destructive">
                Could not load collaborators: {collaboratorsError}
              </span>
            )}
          </label>
        )}

        {/* #1146: mirror of the wizard's Connector-defaults toggle
            (#1127). The toggle is the primary control — while it's
            checked the per-event row is purely informational
            (checkmarks reflect what the server would apply, inputs are
            disabled) and the wire shape omits `events` so the server
            resolves the set itself. Unchecking pre-populates the
            explicit list with the same set the operator was already
            living with. Visually identical to
            `connector-wizard-step.tsx` so the two surfaces don't
            drift; the duplication is intentional (see the file-level
            note about not sharing helpers across the package
            boundary). */}
        <fieldset className="space-y-2">
          <legend className="text-sm text-muted-foreground">
            Webhook events
          </legend>
          <label className="inline-flex cursor-pointer items-center gap-2 text-xs">
            <input
              type="checkbox"
              checked={useDefaults}
              onChange={(e) => {
                const next = e.target.checked;
                setUseDefaults(next);
                if (!next && events.length === 0) {
                  setEvents([...DEFAULT_EVENTS]);
                }
              }}
              data-testid="github-events-use-defaults"
            />
            <span className="font-medium text-foreground">
              Connector defaults
            </span>
          </label>
          <div
            className="flex flex-wrap gap-2"
            aria-label="Webhook events"
            role="group"
          >
            {AVAILABLE_EVENTS.map((e) => {
              const checked = useDefaults
                ? DEFAULT_EVENTS.includes(e)
                : events.includes(e);
              return (
                <label
                  key={e}
                  className={
                    "inline-flex items-center gap-1 rounded-md border border-border px-2 py-1 text-xs " +
                    (useDefaults
                      ? "cursor-not-allowed opacity-70"
                      : "cursor-pointer")
                  }
                >
                  <input
                    type="checkbox"
                    checked={checked}
                    disabled={useDefaults}
                    onChange={() => toggleEvent(e)}
                    aria-label={e}
                  />
                  <span>{e}</span>
                </label>
              );
            })}
          </div>
          <span className="block text-[11px] text-muted-foreground">
            {useDefaults
              ? "The connector subscribes to its default events. Uncheck to pick a custom set."
              : "Custom event set. The server clamps anything unsupported."}
          </span>
        </fieldset>

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
