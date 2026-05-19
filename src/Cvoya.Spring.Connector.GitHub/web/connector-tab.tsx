"use client";

// GitHub connector UI (post-bind). This file lives inside the connector
// package (`src/Cvoya.Spring.Connector.GitHub/web/`), mirroring the
// server-side layout where a connector owns both its .NET code AND its
// web surface.
//
// Turbopack resolves `node_modules` from this out-of-tree location because
// `src/Cvoya.Spring.Web/next.config.ts` sets `turbopack.root` to the
// repository root — see that file for the rationale. The web project
// imports this component through the `@connector-github/*` path alias
// declared in `src/Cvoya.Spring.Web/tsconfig.json`.
//
// ADR-0047 §11 / Phase H reshape (#2508):
//
// * `owner` is gone from the wire shape. The qualified `owner/repo`
//   string is the binding's repo column; this tab exposes a single
//   input that validates the qualified form.
// * The auth-side credential is binding-side and pinned at create time
//   (App installation id OR PAT secret name); the tab surfaces which
//   branch is active and the relevant identifier read-only — the
//   wizard's auth-choice sub-step is where operators change branches.
// * On save, the tab forwards whichever auth field is set on the
//   loaded config; it does not flip branches itself.

import { useCallback, useEffect, useMemo, useState } from "react";
import { Github, KeyRound, Loader2, Lock, RefreshCw, ShieldCheck } from "lucide-react";

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
import { formatTranslatedError } from "@/lib/api/translate-error";
import type {
  GitHubCollaboratorResponse,
  GitHubMissingOAuthResponse,
  GitHubRepositoryResponse,
  UnitGitHubConfigResponse,
} from "@/lib/api/types";
import {
  buildOAuthClientState,
  getAllowedOAuthCallbackOrigins,
  GH_OAUTH_CALLBACK_MESSAGE_TYPE,
  GH_OAUTH_CALLBACK_STORAGE_KEY,
  parseOAuthCallbackPayload,
  parseStoredOAuthCallback,
  readStoredOAuthSessionId,
  writeStoredOAuthSessionId,
} from "./github-oauth-browser";

const GITHUB_APP_DOCS_URL =
  "https://github.com/cvoya-com/spring-voyage/blob/main/docs/guide/deployment.md#optional--connector-credentials";

const NO_REVIEWER = "";

// ADR-0047 §11 qualified-repo validator. Matches the wizard step's
// regex; kept duplicated on purpose (the post-bind and pre-bind
// surfaces don't share helpers across the package boundary — see
// file-level note on connector-wizard-step.tsx).
const QUALIFIED_REPO_RE = /^[^\s/]+\/[^\s/]+$/;

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

/**
 * #1663: extracts the missing-OAuth payload from a 401 ApiError thrown
 * by the connector-scoped `list-repositories` endpoint. Drives the
 * "Link your GitHub account" panel rendered in place of the dropdown.
 */
function extractMissingOAuth(
  err: unknown,
): GitHubMissingOAuthResponse | null {
  if (!(err instanceof ApiError) || err.status !== 401) {
    return null;
  }
  const body = err.body as { missingOAuth?: unknown } | null;
  if (
    body !== null &&
    typeof body === "object" &&
    body.missingOAuth === true
  ) {
    return body as GitHubMissingOAuthResponse;
  }
  return null;
}

const AVAILABLE_EVENTS: readonly string[] = [
  "issues",
  "pull_request",
  "issue_comment",
  "push",
  "release",
];

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

  // ADR-0047 §11: the wire shape carries the qualified `owner/repo`
  // string; the local form mirrors that. `appInstallationId` /
  // `patSecretName` are read off the loaded config; the tab does NOT
  // flip auth branches (the wizard's auth-choice sub-step owns that).
  const [repo, setRepo] = useState("");
  const [installationId, setInstallationId] = useState<number | null>(null);
  const [patSecretName, setPatSecretName] = useState<string>("");
  const [reviewer, setReviewer] = useState("");
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
  const [disabledReason, setDisabledReason] = useState<string | null>(null);
  const [missingOAuth, setMissingOAuth] =
    useState<GitHubMissingOAuthResponse | null>(null);
  const [activeSessionId, setActiveSessionId] = useState<string | null>(
    readStoredOAuthSessionId(),
  );
  const [linkingOAuth, setLinkingOAuth] = useState(false);
  const [awaitingOAuthCallback, setAwaitingOAuthCallback] = useState(false);
  const [oAuthLinkError, setOAuthLinkError] = useState<string | null>(null);

  const applyConfig = useCallback((c: UnitGitHubConfigResponse) => {
    setConfig(c);
    setRepo(c.repo ?? "");
    setInstallationId(
      c.appInstallationId == null ? null : Number(c.appInstallationId),
    );
    setPatSecretName(c.pat_secret_name ?? "");
    setReviewer(c.reviewer ?? "");
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
      const message = formatTranslatedError(err);
      setLoadError(message);
    }
  }, [unitId, applyConfig]);

  const loadRepositories = useCallback(async () => {
    let list: GitHubRepositoryResponse[] = [];
    let disabled: string | null = null;
    let missing: GitHubMissingOAuthResponse | null = null;
    setReposLoading(true);
    try {
      list = await api.listGitHubRepositories(activeSessionId ?? undefined);
      setRepositories(list);
      setReposError(null);
      setDisabledReason(null);
      setMissingOAuth(null);
    } catch (err) {
      missing = extractMissingOAuth(err);
      if (missing !== null) {
        setMissingOAuth(missing);
        setDisabledReason(null);
        setReposError(null);
        setRepositories([]);
      } else {
        disabled = extractDisabledReason(err);
        if (disabled !== null) {
          setDisabledReason(disabled);
          setReposError(null);
          setMissingOAuth(null);
        } else {
          const message = formatTranslatedError(err);
          setReposError(message);
          setDisabledReason(null);
          setMissingOAuth(null);
        }
        setRepositories([]);
      }
    } finally {
      setReposLoading(false);
    }
    if (disabled === null && missing === null && list.length === 0) {
      try {
        const { url } = await api.getGitHubInstallUrl();
        setInstallUrl(url);
      } catch {
        // Banner copy alone is enough.
      }
    }
  }, [activeSessionId]);

  const acceptOAuthSession = useCallback(async (sessionId: string) => {
    writeStoredOAuthSessionId(sessionId);
    setActiveSessionId(sessionId);
    setMissingOAuth(null);
    setOAuthLinkError(null);
    setAwaitingOAuthCallback(false);
    setReposLoading(true);
    try {
      const list = await api.listGitHubRepositories(sessionId);
      setRepositories(list);
      setReposError(null);
    } catch (err) {
      const missing = extractMissingOAuth(err);
      if (missing !== null) {
        writeStoredOAuthSessionId(null);
        setActiveSessionId(null);
        setMissingOAuth(missing);
      } else {
        const message = formatTranslatedError(err);
        setReposError(message);
      }
      setRepositories([]);
    } finally {
      setReposLoading(false);
    }
  }, []);

  const linkGitHubAccount = useCallback(async () => {
    setLinkingOAuth(true);
    setOAuthLinkError(null);
    const popup = window.open(
      "",
      "spring-voyage-github-oauth",
      "popup,width=720,height=760",
    );
    if (popup === null) {
      setOAuthLinkError(
        "Your browser blocked the GitHub authorization window.",
      );
      setLinkingOAuth(false);
      return;
    }
    setAwaitingOAuthCallback(true);
    popup.focus();
    try {
      const result = await api.beginGitHubOAuthAuthorize({
        clientState: buildOAuthClientState(),
      });
      popup.location.href = result.authorizeUrl;
    } catch (err) {
      popup.close();
      setAwaitingOAuthCallback(false);
      const message = formatTranslatedError(err);
      setOAuthLinkError(message);
    } finally {
      setLinkingOAuth(false);
    }
  }, []);

  useEffect(() => {
    const allowedOrigins = getAllowedOAuthCallbackOrigins();
    const handlePayload = (value: unknown) => {
      const payload = parseOAuthCallbackPayload(value);
      if (payload === null) return;
      if (payload.error) {
        setAwaitingOAuthCallback(false);
        setOAuthLinkError(payload.reason ?? payload.error);
        return;
      }
      if (payload.sessionId) {
        void acceptOAuthSession(payload.sessionId);
      }
    };
    const handleMessage = (event: MessageEvent) => {
      if (!allowedOrigins.has(event.origin)) return;
      handlePayload(event.data);
    };
    const handleStorage = (event: StorageEvent) => {
      if (event.key !== GH_OAUTH_CALLBACK_STORAGE_KEY) return;
      const payload = parseStoredOAuthCallback(event.newValue);
      if (payload !== null) {
        handlePayload({
          ...payload,
          type: GH_OAUTH_CALLBACK_MESSAGE_TYPE,
        });
      }
    };

    window.addEventListener("message", handleMessage);
    window.addEventListener("storage", handleStorage);
    return () => {
      window.removeEventListener("message", handleMessage);
      window.removeEventListener("storage", handleStorage);
    };
  }, [acceptOAuthSession]);

  // Re-fetch collaborators whenever the chosen repo changes — only on
  // the App-installation branch (PAT-pinned bindings have no installation
  // id and the collaborators endpoint is App-installation-scoped).
  useEffect(() => {
    const slash = repo.indexOf("/");
    if (
      installationId == null ||
      slash <= 0 ||
      slash === repo.length - 1
    ) {
      setCollaborators(null);
      setCollaboratorsError(null);
      setCollaboratorsLoading(false);
      return;
    }
    const owner = repo.slice(0, slash);
    const repoName = repo.slice(slash + 1);
    let cancelled = false;
    setCollaboratorsLoading(true);
    (async () => {
      try {
        const list = await api.listGitHubCollaborators(
          installationId,
          owner,
          repoName,
        );
        if (cancelled) return;
        setCollaborators(list);
        setCollaboratorsError(null);
      } catch (err) {
        if (cancelled) return;
        const message = formatTranslatedError(err);
        setCollaborators([]);
        setCollaboratorsError(message);
      } finally {
        if (!cancelled) setCollaboratorsLoading(false);
      }
    })();
    return () => {
      cancelled = true;
    };
  }, [installationId, repo]);

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

  const repoValidationError = useMemo<string | null>(() => {
    const trimmed = repo.trim();
    if (trimmed === "") return null;
    if (!QUALIFIED_REPO_RE.test(trimmed)) {
      return "Use the 'owner/repo' form (e.g. octocat/Hello-World).";
    }
    return null;
  }, [repo]);

  const handleSave = async () => {
    setSaveError(null);
    setSaving(true);
    try {
      // ADR-0047 §11: keep the binding's auth field shape; the tab
      // forwards whichever side is set on the loaded config rather
      // than flipping branches itself.
      const resp = await api.putUnitGitHubConfig(unitId, {
        repo: repo.trim(),
        events: useDefaults
          ? undefined
          : events.length > 0
            ? events
            : undefined,
        appInstallationId:
          installationId == null ? undefined : installationId,
        pat_secret_name:
          patSecretName.trim() === "" ? undefined : patSecretName.trim(),
        reviewer: reviewer.trim() === "" ? undefined : reviewer.trim(),
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

  const toggleEvent = (e: string) => {
    setEvents((prev) =>
      prev.includes(e) ? prev.filter((x) => x !== e) : [...prev, e],
    );
  };

  const statusBadge = useMemo(() => {
    if (!config || !config.repo || !config.repo.includes("/")) {
      return <Badge variant="outline">Not configured</Badge>;
    }
    return <Badge variant="outline">{config.repo}</Badge>;
  }, [config]);

  // The repository dropdown only renders rows the App can see; when
  // the persisted binding is bound through PAT or to a repo the App
  // lost visibility into, the dropdown collapses to the placeholder.
  const matchingDropdownRow = useMemo(() => {
    const trimmed = repo.trim();
    if (trimmed === "" || !QUALIFIED_REPO_RE.test(trimmed)) return "";
    if (repositories?.some((r) => r.fullName === trimmed)) {
      return trimmed;
    }
    return "";
  }, [repo, repositories]);

  const handleRepoDropdownChange = (next: string) => {
    if (next === "") {
      // The dropdown's placeholder doesn't clear the operator's
      // current binding — they have to edit the input directly.
      return;
    }
    const match = repositories?.find((r) => r.fullName === next) ?? null;
    if (match === null) return;
    setRepo(match.fullName);
    if (installationId != null) {
      // Auto-snap the installation id when the user re-picks within
      // the App path; PAT-pinned bindings ignore this.
      setInstallationId(Number(match.installationId));
    }
  };

  const authBranch = useMemo<"app" | "pat" | "unset">(() => {
    if (installationId != null) return "app";
    if (patSecretName.trim() !== "") return "pat";
    return "unset";
  }, [installationId, patSecretName]);

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

        {disabledReason === null && missingOAuth !== null && (
          <div
            role="alert"
            className="space-y-3 rounded-md border border-info/50 bg-info/15 px-3 py-2 text-sm text-info"
            data-testid="github-missing-oauth"
          >
            <p className="font-medium">
              Link your GitHub account to manage this connector.
            </p>
            <p className="text-foreground">{missingOAuth.reason}</p>
            <p className="text-xs text-foreground">
              The repository dropdown is filtered to only repos you can
              access on GitHub. Linking your account lets the platform
              intersect its installations with your own permissions.
            </p>
            <div className="flex flex-wrap items-center gap-2">
              <Button
                size="sm"
                variant="outline"
                onClick={() => void linkGitHubAccount()}
                disabled={linkingOAuth}
                aria-busy={linkingOAuth}
                data-testid="github-link-account"
              >
                {linkingOAuth ? (
                  <Loader2 className="mr-1 h-4 w-4 animate-spin" />
                ) : (
                  <Github className="mr-1 h-4 w-4" />
                )}
                {linkingOAuth ? "Opening…" : "Link GitHub account"}
              </Button>
              {missingOAuth.authorizeUrl === null && (
                <span className="text-xs text-muted-foreground">
                  (GitHub OAuth is not configured on this deployment.)
                </span>
              )}
            </div>
            {awaitingOAuthCallback && (
              <p className="text-xs text-foreground">
                Finish authorization in the GitHub window. This tab will
                refresh automatically when GitHub redirects back.
              </p>
            )}
            {oAuthLinkError && (
              <p className="text-xs text-destructive">
                GitHub OAuth flow did not complete: {oAuthLinkError}
              </p>
            )}
          </div>
        )}

        {disabledReason === null &&
          missingOAuth === null &&
          repositories &&
          repositories.length === 0 &&
          authBranch !== "pat" && (
            <div
              role="alert"
              className="rounded-md border border-warning/50 bg-warning/15 px-3 py-2 text-sm text-warning"
            >
              <p className="font-medium">No GitHub repositories visible.</p>
              <p className="mt-1 text-foreground">
                Install the App on your account or organisation, and grant it
                access to at least one repository, or switch the binding to
                the PAT path via the new-unit wizard&apos;s auth-choice step.
              </p>
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

        {disabledReason === null && missingOAuth === null && (
          <label className="block space-y-1">
            <span className="text-sm text-muted-foreground">Repository</span>
            {repositories && repositories.length > 0 && (
              <div className="flex items-center gap-2">
                <select
                  aria-label="Repository (from GitHub App installations)"
                  className="h-9 flex-1 rounded-md border border-input bg-background px-3 text-sm"
                  value={matchingDropdownRow}
                  onChange={(e) =>
                    handleRepoDropdownChange(e.target.value)
                  }
                  disabled={reposLoading}
                >
                  <option value="">Select from App installations…</option>
                  {repositories.map((r) => (
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
            )}
            <input
              type="text"
              aria-label="Repository (qualified owner/repo)"
              data-testid="github-repo-qualified"
              className="h-9 w-full rounded-md border border-input bg-background px-3 text-sm font-mono"
              placeholder="octocat/Hello-World"
              value={repo}
              onChange={(e) => setRepo(e.target.value)}
            />
            {repoValidationError !== null && (
              <span
                className="block text-[11px] text-destructive"
                role="alert"
                data-testid="github-repo-validation"
              >
                {repoValidationError}
              </span>
            )}
            {matchingDropdownRow !== "" &&
              repositories?.find((r) => r.fullName === matchingDropdownRow)
                ?.private && (
                <span className="inline-flex items-center gap-1 text-[11px] text-muted-foreground">
                  <Lock className="h-3 w-3" aria-hidden="true" />
                  Private repository.
                </span>
              )}
          </label>
        )}

        {/* Auth surface (read-only). ADR-0047 §11: the binding's
            auth field is pinned at create time; the tab surfaces
            which branch is active. Operators flip branches via the
            new-unit wizard's auth-choice sub-step (a re-bind is the
            only way to change the active credential). */}
        {disabledReason === null && (
          <div
            className="rounded-md border border-border bg-background p-3 text-sm"
            data-testid="github-auth-summary"
          >
            <p className="text-xs font-medium uppercase tracking-wide text-muted-foreground">
              Auth choice
            </p>
            {authBranch === "app" && (
              <p className="mt-1 flex items-center gap-2 text-foreground">
                <ShieldCheck
                  className="h-4 w-4 text-muted-foreground"
                  aria-hidden="true"
                />
                App installation
                <code className="rounded bg-muted px-1 py-0.5 text-[11px]">
                  {installationId}
                </code>
              </p>
            )}
            {authBranch === "pat" && (
              <p className="mt-1 flex items-center gap-2 text-foreground">
                <KeyRound
                  className="h-4 w-4 text-muted-foreground"
                  aria-hidden="true"
                />
                PAT secret
                <code className="rounded bg-muted px-1 py-0.5 text-[11px]">
                  {patSecretName}
                </code>
              </p>
            )}
            {authBranch === "unset" && (
              <p className="mt-1 text-muted-foreground">
                Not configured. Re-bind via the new-unit wizard&apos;s
                auth-choice step to pick App installation or PAT secret.
              </p>
            )}
            <p className="mt-2 text-[11px] text-muted-foreground">
              The binding pins one outbound credential; switching
              branches requires re-binding the unit.
            </p>
          </div>
        )}

        {disabledReason === null &&
          missingOAuth === null &&
          authBranch === "app" &&
          installationId != null && (
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

        {disabledReason === null &&
          missingOAuth === null &&
          authBranch === "pat" && (
            <label className="block space-y-1">
              <span className="text-sm text-muted-foreground">
                Default reviewer
              </span>
              <input
                type="text"
                aria-label="Default reviewer (PAT path)"
                className="h-9 w-full rounded-md border border-input bg-background px-3 text-sm"
                placeholder="GitHub login (optional)"
                value={reviewer}
                onChange={(e) => setReviewer(e.target.value)}
              />
              <span className="block text-[11px] text-muted-foreground">
                Collaborator lookup is App-installation-scoped; on the
                PAT path you type the reviewer login manually.
              </span>
            </label>
          )}

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
            disabled={
              saving ||
              repo.trim() === "" ||
              repoValidationError !== null
            }
          >
            {saving && <Loader2 className="mr-1 h-4 w-4 animate-spin" />}
            {saving ? "Saving…" : "Save"}
          </Button>
        </div>
      </CardContent>
    </Card>
  );
}
