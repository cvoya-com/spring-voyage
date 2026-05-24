"use client";

// GitHub connector wizard-step UI. This lives alongside `connector-tab.tsx`
// in the connector package so the .NET connector owns both the post-bind
// management surface (the tab) AND the pre-bind wizard surface (this file).
//
// The two components are deliberately separate (see #199):
//
// * `connector-tab.tsx` — mounted on /units/[id] for an already-bound unit.
//   Loads existing config and installations from the live actor.
// * `connector-wizard-step.tsx` — mounted inside the create-unit wizard
//   before the unit exists. No unit id, no live config — it's a pure form
//   that produces a config payload the wizard bundles into the single
//   create-unit call.
//
// The host web app resolves this file via the `@connector-github/*` path
// alias declared in `src/Cvoya.Spring.Web/tsconfig.json`. It's listed
// alongside `connector-tab.tsx` in `src/Cvoya.Spring.Web/src/connectors/
// registry.ts` so both entry points are statically known at build time.
//
// ADR-0047 §11 / Phase H reshape (#2508):
//
// * `owner` is gone from the wire shape (`UnitGitHubConfigRequest.repo`
//   carries the qualified `owner/repo` string). The wizard surfaces a
//   single qualified-repo input — picking a row from the repository
//   dropdown is the primary path; manual entry is accepted but
//   validated against the `owner/repo` shape at form-validation time.
// * The auth-choice sub-step (ADR-0047 §6 / §11): the wizard explicitly
//   prompts for App installation OR PAT secret before letting the
//   operator pass the step. App installation is auto-selected when the
//   chosen row carries an `installationId`; the operator can override
//   to PAT (either OAuth-acquired or paste-an-existing-name).
// * 400 GitHubBindingAuthRequired / GitHubBindingAuthAmbiguous and 409
//   GitHubCrossTenantRepoBindingConflict surface as inline messages on
//   the install step (the wizard renders the bubbled error).

import { useCallback, useEffect, useMemo, useRef, useState } from "react";
import { Github, KeyRound, Loader2, RefreshCw, ShieldCheck } from "lucide-react";

import { Button } from "@/components/ui/button";
import { ApiError, api } from "@/lib/api/client";
import { formatTranslatedError } from "@/lib/api/translate-error";
import type {
  GitHubCollaboratorResponse,
  GitHubMissingOAuthResponse,
  GitHubRepositoryResponse,
  UnitGitHubConfigRequest,
} from "@/lib/api/types";
import {
  buildOAuthClientState,
  getAllowedOAuthCallbackOrigins,
  GH_OAUTH_CALLBACK_MESSAGE_TYPE,
  GH_OAUTH_CALLBACK_STORAGE_KEY,
  mintBindingId,
  parseOAuthCallbackPayload,
  parseStoredOAuthCallback,
  readStoredOAuthSessionId,
  writeStoredOAuthSessionId,
} from "./github-oauth-browser";

// Documentation anchor we surface in the disabled-with-reason panel so
// operators can self-serve the credential set-up. Kept in one place — if
// the deployment guide moves, only this constant changes.
const GITHUB_APP_DOCS_URL =
  "https://github.com/cvoya-com/spring-voyage/blob/main/docs/guide/deployment.md#optional--connector-credentials";

// Sentinel value for the Reviewer dropdown's "no default reviewer" row.
// The empty string is what the underlying <select> emits for an empty
// option, and it can never collide with a real GitHub login.
const NO_REVIEWER = "";

/**
 * Issue #2563: split a free-text textarea into a list of label patterns.
 * Splits on newlines and commas, trims, drops empties, de-duplicates
 * case-insensitively. Kept duplicated with the post-bind tab on purpose
 * — the two surfaces don't share helpers (see file-level note).
 */
function parseLabelPatterns(text: string): string[] {
  const seen = new Set<string>();
  const out: string[] = [];
  for (const piece of text.split(/[\n,]+/)) {
    const trimmed = piece.trim();
    if (trimmed === "") continue;
    const key = trimmed.toLowerCase();
    if (seen.has(key)) continue;
    seen.add(key);
    out.push(trimmed);
  }
  return out;
}

// `owner/repo` validator (ADR-0047 §11). One slash, both segments
// non-empty, no leading/trailing whitespace. The server clamps
// stricter; the inline message gives the operator a fast-feedback hint
// before they hit Next.
const QUALIFIED_REPO_RE = /^[^\s/]+\/[^\s/]+$/;

interface ConnectorDisabledProblem {
  disabled: true;
  reason: string;
}

function isConnectorDisabledProblem(
  body: unknown,
): body is ConnectorDisabledProblem {
  return (
    typeof body === "object" &&
    body !== null &&
    "disabled" in body &&
    (body as { disabled?: unknown }).disabled === true
  );
}

function extractDisabledReason(err: unknown): string | null {
  if (!(err instanceof ApiError) || err.status !== 404) {
    return null;
  }
  if (isConnectorDisabledProblem(err.body)) {
    return err.body.reason;
  }
  return null;
}

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

/**
 * Auth-choice discriminator (ADR-0047 §11). Either an App installation
 * id or a PAT secret name lands on the binding — exactly one. Tracked
 * locally so the radio control's selection state survives the OAuth
 * round-trip even when `installationId` is reset between repo picks.
 */
type AuthChoice = "app" | "pat";

export interface GitHubConnectorWizardStepProps {
  /**
   * Fires whenever the form produces a new valid config payload (or `null`
   * when the form is incomplete). The wizard listens to this and stores
   * the latest payload; on the Install step it bundles it into the
   * create-unit call.
   */
  onChange: (body: UnitGitHubConfigRequest | null) => void;

  /**
   * Initial values for the form — used when the user navigates back to the
   * wizard step after having filled it out once. Optional.
   */
  initialValue?: UnitGitHubConfigRequest | null;

  /**
   * #1663: caller-supplied override of the GitHub OAuth session id. When
   * omitted the component falls back to the session id cached in
   * `sessionStorage` (populated by the in-panel "Link GitHub account"
   * flow). Pass-through only — the wizard host does not currently inject
   * one, but the prop is kept so a cloud overlay can plumb its own
   * single-sign-on session through.
   */
  gitHubSessionId?: string;
}

/**
 * Wizard-mode GitHub connector configuration. Renders the qualified
 * repository input (with a repository dropdown for App-detected rows),
 * an auth-choice sub-step (App installation vs. PAT secret), and the
 * reviewer + events controls. Bubbles a {@link UnitGitHubConfigRequest}
 * up to the parent wizard.
 */
export function GitHubConnectorWizardStep({
  onChange,
  initialValue,
  gitHubSessionId,
}: GitHubConnectorWizardStepProps) {
  // ADR-0047 §11 reshape: a single qualified-repo input. The wizard no
  // longer splits `owner/repo` locally; the form value is the
  // qualified string the wire shape carries.
  const [repo, setRepo] = useState(initialValue?.repo ?? "");
  const [installationId, setInstallationId] = useState<number | null>(
    initialValue?.appInstallationId == null
      ? null
      : Number(initialValue.appInstallationId),
  );
  const [patSecretName, setPatSecretName] = useState<string>(
    initialValue?.pat_secret_name ?? "",
  );

  // Auth choice seeded from the initial value when present (the
  // operator may navigate back to the step after picking a path on the
  // first visit). Default is "app" — the historical OSS default and
  // the path with the simplest UX when the repository dropdown
  // populates.
  const seededAuthChoice: AuthChoice =
    initialValue?.pat_secret_name && initialValue.pat_secret_name.length > 0
      ? "pat"
      : "app";
  const [authChoice, setAuthChoice] = useState<AuthChoice>(seededAuthChoice);

  const [reviewer, setReviewer] = useState(initialValue?.reviewer ?? "");
  const initialUseDefaults =
    initialValue?.events == null || initialValue.events.length === 0;
  const [useDefaults, setUseDefaults] = useState<boolean>(initialUseDefaults);
  const [events, setEvents] = useState<string[]>(
    initialValue?.events && initialValue.events.length > 0
      ? [...initialValue.events]
      : [...DEFAULT_EVENTS],
  );
  // Issue #2563: per-binding inbound label filter. Each textarea is one
  // pattern per line (commas also accepted); seeded from initialValue
  // so a back-nav into this step preserves the previous edits.
  const [includeLabelsText, setIncludeLabelsText] = useState<string>(
    (initialValue?.include_labels ?? []).join("\n"),
  );
  const [excludeLabelsText, setExcludeLabelsText] = useState<string>(
    (initialValue?.exclude_labels ?? []).join("\n"),
  );

  const [repositories, setRepositories] = useState<
    GitHubRepositoryResponse[] | null
  >(null);
  const [reposError, setReposError] = useState<string | null>(null);
  const [reposLoading, setReposLoading] = useState(true);

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
    gitHubSessionId ?? readStoredOAuthSessionId(),
  );
  const [linkingOAuth, setLinkingOAuth] = useState(false);
  const [awaitingOAuthCallback, setAwaitingOAuthCallback] = useState(false);
  const [oAuthLinkError, setOAuthLinkError] = useState<string | null>(null);
  const [rechecking, setRechecking] = useState(false);

  // OAuth-for-PAT state (ADR-0047 §13). The auth-choice sub-step pre-
  // mints a binding UUID before opening the popup so the OAuth
  // callback writes its secret under `binding/<id-no-dash>/github/
  // pat`; the wizard reuses the same id on the binding-create call.
  // We keep the minted id alive across re-renders so the operator can
  // retry the popup without re-allocating.
  const pendingBindingIdRef = useRef<string | null>(null);
  const [authorizingPat, setAuthorizingPat] = useState(false);
  const [awaitingPatCallback, setAwaitingPatCallback] = useState(false);
  const [patAuthorizeError, setPatAuthorizeError] = useState<string | null>(
    null,
  );

  const fetchRepositories = useCallback(async () => {
    let list: GitHubRepositoryResponse[] = [];
    let disabled: string | null = null;
    let missing: GitHubMissingOAuthResponse | null = null;
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
          setReposError(formatTranslatedError(err));
          setDisabledReason(null);
          setMissingOAuth(null);
        }
        setRepositories([]);
      }
    }
    if (disabled === null && missing === null && list.length === 0) {
      try {
        const { url } = await api.getGitHubInstallUrl();
        setInstallUrl(url);
      } catch {
        // Swallow — the banner already tells the user what's wrong.
      }
    }
  }, [activeSessionId]);

  const recheckRepositories = useCallback(async () => {
    setRechecking(true);
    try {
      await fetchRepositories();
    } finally {
      setRechecking(false);
    }
  }, [fetchRepositories]);

  const acceptOAuthSession = useCallback(async (sessionId: string) => {
    writeStoredOAuthSessionId(sessionId);
    setActiveSessionId(sessionId);
    setMissingOAuth(null);
    setOAuthLinkError(null);
    setAwaitingOAuthCallback(false);
    setRechecking(true);
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
        setReposError(formatTranslatedError(err));
      }
      setRepositories([]);
    } finally {
      setRechecking(false);
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
      setOAuthLinkError(formatTranslatedError(err));
    } finally {
      setLinkingOAuth(false);
    }
  }, []);

  /**
   * Pre-mints a binding UUID, opens the OAuth popup with the
   * `binding-wizard` intent, and waits for the callback page's
   * postMessage / localStorage handoff. The handoff carries the
   * persisted `patSecretName` + `bindingId` (ADR-0047 §13); the local
   * state is updated as soon as the payload arrives.
   */
  const authorizePat = useCallback(async () => {
    setPatAuthorizeError(null);
    setAuthorizingPat(true);
    const popup = window.open(
      "",
      "spring-voyage-github-oauth-pat",
      "popup,width=720,height=760",
    );
    if (popup === null) {
      setPatAuthorizeError(
        "Your browser blocked the GitHub authorization window.",
      );
      setAuthorizingPat(false);
      return;
    }
    setAwaitingPatCallback(true);
    popup.focus();
    try {
      const bindingId = mintBindingId();
      pendingBindingIdRef.current = bindingId;
      const result = await api.beginGitHubOAuthAuthorize({
        clientState: buildOAuthClientState(),
        intent: "binding-wizard",
        bindingId,
      });
      popup.location.href = result.authorizeUrl;
    } catch (err) {
      popup.close();
      setAwaitingPatCallback(false);
      pendingBindingIdRef.current = null;
      setPatAuthorizeError(formatTranslatedError(err));
    } finally {
      setAuthorizingPat(false);
    }
  }, []);

  useEffect(() => {
    const allowedOrigins = getAllowedOAuthCallbackOrigins();
    const handlePayload = (value: unknown) => {
      const payload = parseOAuthCallbackPayload(value);
      if (payload === null) return;
      if (payload.error) {
        setAwaitingOAuthCallback(false);
        setAwaitingPatCallback(false);
        // Route the error to whichever flow is in flight.
        if (pendingBindingIdRef.current !== null) {
          setPatAuthorizeError(payload.reason ?? payload.error);
          pendingBindingIdRef.current = null;
        } else {
          setOAuthLinkError(payload.reason ?? payload.error);
        }
        return;
      }
      // Binding-wizard handoff: persist the secret name + binding id on
      // the local form so the wizard's binding-create call rides
      // through the PAT branch.
      if (payload.patSecretName) {
        setPatSecretName(payload.patSecretName);
        setAuthChoice("pat");
        // Clear App-side state — the binding's auth is mutually-
        // exclusive (ADR-0047 §11) and the form's onChange must not
        // bubble both fields.
        setInstallationId(null);
        setAwaitingPatCallback(false);
        pendingBindingIdRef.current = null;
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

  useEffect(() => {
    let cancelled = false;
    setReposLoading(true);
    (async () => {
      try {
        await fetchRepositories();
      } finally {
        if (!cancelled) setReposLoading(false);
      }
    })();
    return () => {
      cancelled = true;
    };
  }, [fetchRepositories]);

  // Collaborators (re-fetched whenever the repo selection changes).
  // ADR-0047 §11: the qualified `owner/repo` string is what the wire
  // carries; the collaborators endpoint still takes owner+repo
  // separately so we split locally before the call. `installationId`
  // is required by the collaborators endpoint; when the operator
  // picked the PAT path (no installation id) we skip the call — the
  // dropdown collapses to the manual "(none)" row.
  useEffect(() => {
    const slash = repo.indexOf("/");
    if (
      authChoice !== "app" ||
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
        setCollaborators([]);
        setCollaboratorsError(formatTranslatedError(err));
      } finally {
        if (!cancelled) setCollaboratorsLoading(false);
      }
    })();
    return () => {
      cancelled = true;
    };
  }, [authChoice, installationId, repo]);

  // Form-validation message — non-null when the operator has typed a
  // repo string but it does not match the qualified shape. Drives the
  // inline hint and gates the onChange bubble below.
  const repoValidationError = useMemo<string | null>(() => {
    const trimmed = repo.trim();
    if (trimmed === "") return null;
    if (!QUALIFIED_REPO_RE.test(trimmed)) {
      return "Use the 'owner/repo' form (e.g. octocat/Hello-World).";
    }
    return null;
  }, [repo]);

  // Bubble validated state up to the wizard. Null when the form is
  // incomplete or the auth-choice gate is unsatisfied.
  useEffect(() => {
    const trimmedRepo = repo.trim();
    if (trimmedRepo === "" || repoValidationError !== null) {
      onChange(null);
      return;
    }
    // ADR-0047 §11: exactly one of App installation / PAT secret. The
    // wizard refuses to bundle a payload until the auth-choice is
    // resolved.
    const includeLabels = parseLabelPatterns(includeLabelsText);
    const excludeLabels = parseLabelPatterns(excludeLabelsText);
    if (authChoice === "app") {
      if (installationId == null) {
        onChange(null);
        return;
      }
      onChange({
        repo: trimmedRepo,
        appInstallationId: installationId,
        events: useDefaults
          ? undefined
          : events.length > 0
            ? events
            : undefined,
        reviewer: reviewer.trim() === "" ? undefined : reviewer.trim(),
        include_labels: includeLabels.length > 0 ? includeLabels : undefined,
        exclude_labels: excludeLabels.length > 0 ? excludeLabels : undefined,
      });
      return;
    }
    // PAT path. The secret-name field must be non-empty before the
    // wizard advances; OAuth-completion writes the name automatically.
    const trimmedSecret = patSecretName.trim();
    if (trimmedSecret === "") {
      onChange(null);
      return;
    }
    onChange({
      repo: trimmedRepo,
      pat_secret_name: trimmedSecret,
      events: useDefaults
        ? undefined
        : events.length > 0
          ? events
          : undefined,
      reviewer: reviewer.trim() === "" ? undefined : reviewer.trim(),
      include_labels: includeLabels.length > 0 ? includeLabels : undefined,
      exclude_labels: excludeLabels.length > 0 ? excludeLabels : undefined,
    });
  }, [
    repo,
    repoValidationError,
    authChoice,
    installationId,
    patSecretName,
    events,
    reviewer,
    useDefaults,
    includeLabelsText,
    excludeLabelsText,
    onChange,
  ]);

  const toggleEvent = (e: string) => {
    setEvents((prev) =>
      prev.includes(e) ? prev.filter((x) => x !== e) : [...prev, e],
    );
  };

  const handleRepoDropdownChange = (next: string) => {
    if (next === "") {
      setRepo("");
      setInstallationId(null);
      setReviewer("");
      return;
    }
    const match = repositories?.find((r) => r.fullName === next) ?? null;
    if (match === null) return;
    setRepo(match.fullName);
    // ADR-0047 §11: the App-installation id is the binding's
    // credential when the operator stays on the App branch. Set it
    // whenever a row is picked; the operator can flip to PAT later
    // (we clear `installationId` in `setAuthChoice` for the radio).
    setInstallationId(Number(match.installationId));
    if (authChoice === "app") {
      setReviewer("");
    }
  };

  const handleAuthChoiceChange = (next: AuthChoice) => {
    setAuthChoice(next);
    if (next === "app") {
      // Switching to App clears any pending PAT state — the binding's
      // auth is single-valued.
      setPatSecretName("");
      pendingBindingIdRef.current = null;
      setAwaitingPatCallback(false);
    } else {
      // Switching to PAT clears the App installation id so the wire
      // shape only carries the chosen field.
      setInstallationId(null);
      setReviewer("");
    }
  };

  const repoBusy = reposLoading || rechecking;

  // The repository dropdown only renders rows the operator can pick
  // *and* the App has visibility into. When the qualified-repo input
  // doesn't match a row, the dropdown collapses to the placeholder
  // option — the operator is free-typing.
  const matchingDropdownRow = useMemo(() => {
    const trimmed = repo.trim();
    if (trimmed === "" || !QUALIFIED_REPO_RE.test(trimmed)) return "";
    if (repositories?.some((r) => r.fullName === trimmed)) {
      return trimmed;
    }
    return "";
  }, [repo, repositories]);

  return (
    <div className="space-y-4 rounded-md border border-border bg-muted/30 p-4">
      <div className="flex items-center gap-2">
        <Github className="h-4 w-4" />
        <span className="text-sm font-medium">GitHub connector</span>
      </div>

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
            (or run{" "}
            <code className="rounded bg-muted px-1 py-0.5 text-[11px]">
              spring github-app register
            </code>
            ) before this connector can be bound.
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
          <p className="font-medium">Link your GitHub account to continue.</p>
          <p className="text-foreground">{missingOAuth.reason}</p>
          <p className="text-xs text-foreground">
            The repository dropdown is filtered to only repos you can access
            on GitHub. Linking your account lets the platform intersect
            its installations with your own permissions, so private repos
            you don&apos;t have access to never appear here.
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
                (GitHub OAuth is not configured on this deployment — ask an
                operator to set <code>GitHub:OAuth:ClientId</code> /{" "}
                <code>ClientSecret</code> / <code>RedirectUri</code>.)
              </span>
            )}
          </div>
          {awaitingOAuthCallback && (
            <p className="text-xs text-foreground">
              Finish authorization in the GitHub window. This step will
              continue automatically when GitHub redirects back.
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
        repositories.length === 0 && (
          <div
            role="alert"
            className="rounded-md border border-warning/50 bg-warning/15 px-3 py-2 text-sm text-warning"
          >
            <p className="font-medium">No GitHub repositories visible.</p>
            <p className="mt-1 text-foreground">
              Install the GitHub App on a repository the unit will write to,
              or pick the &quot;Use a PAT secret&quot; auth choice below and
              type the qualified <code>owner/repo</code> manually.
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
                onClick={() => void recheckRepositories()}
                disabled={rechecking}
                aria-label="Recheck installations"
                aria-busy={rechecking}
                data-testid="github-recheck-installations"
              >
                {rechecking ? (
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
                {rechecking ? "Rechecking…" : "Recheck installations"}
                {rechecking && (
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
        <>
          {/* Repository (qualified `owner/repo`). The dropdown is
              populated from the App-visible repositories; manual entry
              is accepted when the operator is on the PAT branch (or
              the App simply has no visibility into the target repo
              yet). ADR-0047 §11 dropped the owner field; the single
              input carries the qualified string. */}
          <label className="block space-y-1">
            <span className="text-xs text-muted-foreground">
              Repository<span className="text-destructive"> *</span>
            </span>
            {repositories && repositories.length > 0 && (
              <div className="flex items-center gap-2">
                <select
                  aria-label="Repository (from GitHub App installations)"
                  className="h-9 flex-1 rounded-md border border-input bg-background px-3 text-sm"
                  value={matchingDropdownRow}
                  onChange={(e) =>
                    handleRepoDropdownChange(e.target.value)
                  }
                  disabled={repoBusy}
                >
                  <option value="">
                    {repoBusy
                      ? "Loading repositories…"
                      : "Select from App installations…"}
                  </option>
                  {repositories?.map((r) => (
                    <option
                      key={`${r.installationId}:${r.repositoryId}`}
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
                  onClick={() => void recheckRepositories()}
                  aria-label="Refresh repositories"
                  aria-busy={repoBusy}
                  disabled={repoBusy}
                >
                  {repoBusy ? (
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
              onChange={(e) => {
                setRepo(e.target.value);
                // Free-typing breaks the dropdown selection so the
                // installation id no longer auto-fills. Clear it; the
                // operator either re-picks from the dropdown or
                // switches to the PAT branch.
                if (
                  !repositories?.some((r) => r.fullName === e.target.value)
                ) {
                  setInstallationId(null);
                }
              }}
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
            <span className="block text-[11px] text-muted-foreground">
              ADR-0047 §11: bindings store the qualified
              <code className="mx-1 rounded bg-muted px-1 py-0.5 text-[11px]">
                owner/repo
              </code>
              form.
              {matchingDropdownRow !== "" && (
                <>
                  {" "}
                  Picked from App installation
                  <code className="mx-1 rounded bg-muted px-1 py-0.5 text-[11px]">
                    {installationId}
                  </code>
                  .
                </>
              )}
            </span>
          </label>

          {/* Auth-choice sub-step (ADR-0047 §§ 6, 11). Exactly one of
              App installation / PAT secret lands on the binding; the
              wizard surfaces the trade-off explicitly so the operator
              picks deliberately. The two branches share validation
              wiring above — the wire payload is gated on the chosen
              branch having a usable value. */}
          <fieldset
            className="space-y-2 rounded-md border border-border bg-background p-3"
            data-testid="github-auth-choice"
          >
            <legend className="px-1 text-xs font-medium text-muted-foreground">
              Auth choice
            </legend>
            <p className="text-[11px] text-muted-foreground">
              The binding pins one outbound credential at create time
              (ADR-0047 §11). Pick App installation when the SV App is
              installed on the repo; pick PAT secret for repos the App
              is not installed on (e.g. public repos, operator-
              controlled credentials).
            </p>
            <label className="flex cursor-pointer items-start gap-2 rounded-md border border-border p-2 text-sm">
              <input
                type="radio"
                name="github-auth-choice"
                value="app"
                checked={authChoice === "app"}
                onChange={() => handleAuthChoiceChange("app")}
                data-testid="github-auth-choice-app"
                className="mt-1"
              />
              <span className="flex-1">
                <span className="inline-flex items-center gap-1 font-medium">
                  <ShieldCheck
                    className="h-3.5 w-3.5"
                    aria-hidden="true"
                  />
                  Use an App installation
                </span>
                <span className="block text-[11px] text-muted-foreground">
                  Outbound writes mint installation tokens for the
                  picked App. Pick a row from the repository dropdown
                  above to auto-fill the installation id.
                </span>
                {authChoice === "app" && (
                  <span className="mt-1 block text-[11px] text-muted-foreground">
                    Installation id:{" "}
                    <code className="rounded bg-muted px-1 py-0.5 text-[11px]">
                      {installationId ?? "(none selected)"}
                    </code>
                  </span>
                )}
              </span>
            </label>
            <label className="flex cursor-pointer items-start gap-2 rounded-md border border-border p-2 text-sm">
              <input
                type="radio"
                name="github-auth-choice"
                value="pat"
                checked={authChoice === "pat"}
                onChange={() => handleAuthChoiceChange("pat")}
                data-testid="github-auth-choice-pat"
                className="mt-1"
              />
              <span className="flex-1">
                <span className="inline-flex items-center gap-1 font-medium">
                  <KeyRound className="h-3.5 w-3.5" aria-hidden="true" />
                  Use a PAT secret
                </span>
                <span className="block text-[11px] text-muted-foreground">
                  Outbound writes use a tenant secret addressing a
                  personal access token (ADR-0047 §5). Recommended
                  path: authorize via GitHub (the OAuth flow writes the
                  secret automatically). Alternative: paste an existing
                  tenant secret name.
                </span>
                {authChoice === "pat" && (
                  <span className="mt-2 block space-y-2">
                    <Button
                      type="button"
                      size="sm"
                      variant="outline"
                      onClick={() => void authorizePat()}
                      disabled={authorizingPat}
                      data-testid="github-pat-authorize"
                      aria-busy={authorizingPat}
                    >
                      {authorizingPat ? (
                        <Loader2 className="mr-1 h-4 w-4 animate-spin" />
                      ) : (
                        <Github className="mr-1 h-4 w-4" />
                      )}
                      {authorizingPat
                        ? "Opening…"
                        : "Authorize with GitHub"}
                    </Button>
                    {awaitingPatCallback && (
                      <span className="block text-[11px] text-muted-foreground">
                        Finish authorization in the GitHub window. The
                        secret name will fill in automatically.
                      </span>
                    )}
                    {patAuthorizeError && (
                      <span className="block text-[11px] text-destructive">
                        Authorization did not complete:{" "}
                        {patAuthorizeError}
                      </span>
                    )}
                    <input
                      type="text"
                      aria-label="PAT secret name"
                      data-testid="github-pat-secret-name"
                      className="h-9 w-full rounded-md border border-input bg-background px-3 text-sm font-mono"
                      placeholder="binding/<id>/github/pat (or paste an existing tenant secret name)"
                      value={patSecretName}
                      onChange={(e) => setPatSecretName(e.target.value)}
                    />
                    <span className="block text-[11px] text-muted-foreground">
                      The tenant secret name the binding stores
                      (ADR-0047 §5). The OAuth flow writes
                      <code className="mx-1 rounded bg-muted px-1 py-0.5 text-[11px]">
                        binding/&lt;id&gt;/github/pat
                      </code>
                      ; pasting an existing name overrides the default.
                    </span>
                  </span>
                )}
              </span>
            </label>
          </fieldset>
        </>
      )}

      {disabledReason === null &&
        missingOAuth === null &&
        authChoice === "app" &&
        installationId != null && (
          <label className="block space-y-1">
            <span className="text-xs text-muted-foreground">
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
            <span className="block text-[11px] text-muted-foreground">
              Requested as the reviewer when this unit&apos;s agents open
              pull requests. Optional — agents that pass a reviewer
              explicitly still override per-call.
            </span>
          </label>
        )}

      {disabledReason === null && missingOAuth === null && authChoice === "pat" && (
        <label className="block space-y-1">
          <span className="text-xs text-muted-foreground">
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
            Collaborator lookup is App-installation-scoped; on the PAT
            path you type the reviewer login manually.
          </span>
        </label>
      )}

      {/* Issue #2563: per-binding label filter (optional). One pattern
          per line — `*`, `prefix:*`, or an exact label name. Exclude
          patterns are evaluated first and short-circuit to a drop. */}
      <fieldset
        className="space-y-3"
        data-testid="github-label-filters"
      >
        <legend className="text-xs text-muted-foreground">
          Label filters
        </legend>
        <p className="text-[11px] text-muted-foreground">
          Optional. One pattern per line (commas also accepted). Supports{" "}
          <code className="rounded bg-muted px-1 py-0.5">*</code> for all
          labels and{" "}
          <code className="rounded bg-muted px-1 py-0.5">prefix:*</code>{" "}
          for namespaced label families.
        </p>
        <label className="block space-y-1">
          <span className="text-xs text-muted-foreground">
            Include labels (allow-list)
          </span>
          <textarea
            aria-label="Include labels (one pattern per line)"
            data-testid="github-include-labels"
            className="min-h-[56px] w-full rounded-md border border-input bg-background px-3 py-2 text-sm font-mono"
            placeholder={"spring-voyage-team:*\nbug"}
            value={includeLabelsText}
            onChange={(e) => setIncludeLabelsText(e.target.value)}
          />
          <span className="block text-[11px] text-muted-foreground">
            Events pass only if at least one of their labels matches one
            of these patterns. Leave empty to skip the allow-list check.
          </span>
        </label>
        <label className="block space-y-1">
          <span className="text-xs text-muted-foreground">
            Exclude labels (block-list)
          </span>
          <textarea
            aria-label="Exclude labels (one pattern per line)"
            data-testid="github-exclude-labels"
            className="min-h-[56px] w-full rounded-md border border-input bg-background px-3 py-2 text-sm font-mono"
            placeholder={"wip\ninternal:*"}
            value={excludeLabelsText}
            onChange={(e) => setExcludeLabelsText(e.target.value)}
          />
          <span className="block text-[11px] text-muted-foreground">
            Events whose labels match any of these patterns are dropped.
          </span>
        </label>
      </fieldset>

      <fieldset className="space-y-2">
        <legend className="text-xs text-muted-foreground">
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
    </div>
  );
}
