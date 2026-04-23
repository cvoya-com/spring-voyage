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
// #1133: the surface dropped manual owner / repo / installation pickers in
// favour of a single Repository dropdown sourced from the aggregated
// `/list-repositories` endpoint, plus a Reviewer dropdown sourced from
// `/list-collaborators` for the chosen repo. The installation id is no
// longer user-visible — it rides along on every repository row so the
// wire shape stays the same.

import { useCallback, useEffect, useState } from "react";
import { Github, Loader2, Lock, RefreshCw } from "lucide-react";

import { Button } from "@/components/ui/button";
import { ApiError, api } from "@/lib/api/client";
import type {
  GitHubCollaboratorResponse,
  GitHubRepositoryResponse,
  UnitGitHubConfigRequest,
} from "@/lib/api/types";

// Documentation anchor we surface in the disabled-with-reason panel so
// operators can self-serve the credential set-up. Kept in one place — if
// the deployment guide moves, only this constant changes.
const GITHUB_APP_DOCS_URL =
  "https://github.com/cvoya-com/spring-voyage/blob/main/docs/guide/deployment.md#optional--connector-credentials";

// Sentinel value for the Reviewer dropdown's "no default reviewer" row.
// The empty string is what the underlying <select> emits for an empty
// option, and it can never collide with a real GitHub login.
const NO_REVIEWER = "";

// Shape of the Problem+JSON the GitHub connector returns when the App
// credentials are not configured at the deployment level (#609 / #1186).
// The actor and the wizard speak the same contract: `disabled: true` plus
// a human-readable `reason`. The wizard turns that into a friendly panel
// instead of leaking the raw RFC 9110 envelope through `err.message`.
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

/**
 * Extracts the disabled-with-reason payload from an {@link ApiError} thrown
 * by the connector-scoped GitHub endpoints. Returns `null` for any other
 * shape — the caller falls back to the generic error path.
 */
function extractDisabledReason(err: unknown): string | null {
  if (!(err instanceof ApiError) || err.status !== 404) {
    return null;
  }
  if (isConnectorDisabledProblem(err.body)) {
    return err.body.reason;
  }
  return null;
}

/**
 * #1153: shape of the Problem+JSON the connector returns when no
 * signed-in GitHub user is available on the request. The wizard uses
 * the presence of `requires_signin: true` to render the "Sign in with
 * GitHub" affordance instead of the generic error path.
 */
function isRequiresSigninProblem(err: unknown): boolean {
  if (!(err instanceof ApiError) || err.status !== 401) {
    return false;
  }
  const body = err.body as { requires_signin?: unknown } | null;
  return (
    typeof body === "object" &&
    body !== null &&
    body.requires_signin === true
  );
}

/**
 * `sessionStorage` key the wizard uses to persist the OAuth session id
 * across the redirect back from GitHub. Scoped to a constant so the
 * post-callback handler in the layout can clear it deterministically.
 */
const GITHUB_OAUTH_SESSION_KEY = "spring.connectors.github.oauthSessionId";

// Mirror of the event set in connector-tab.tsx. Kept duplicated on purpose
// — changing the set of offered events in one surface shouldn't silently
// change it in the other. The server clamps anything the user picks to the
// connector's known-safe list.
const AVAILABLE_EVENTS: readonly string[] = [
  "issues",
  "pull_request",
  "issue_comment",
  "push",
  "release",
];

// Mirror of `GitHubConnectorType.DefaultEvents`. The server falls back to
// this set whenever the wire `events` field is null or empty, so the
// wizard surfaces the same list as the informational row under the
// "Connector defaults" toggle. Kept duplicated on purpose — changing the
// defaults in one place shouldn't silently change them in the other.
// (#1127)
const DEFAULT_EVENTS: readonly string[] = [
  "issues",
  "pull_request",
  "issue_comment",
];

export interface GitHubConnectorWizardStepProps {
  /**
   * Fires whenever the form produces a new valid config payload (or `null`
   * when the form is incomplete). The wizard listens to this and stores
   * the latest payload; on Step 5 it bundles it into the create-unit call.
   */
  onChange: (body: UnitGitHubConfigRequest | null) => void;

  /**
   * Initial values for the form — used when the user navigates back to the
   * wizard step after having filled it out once. Optional.
   */
  initialValue?: UnitGitHubConfigRequest | null;
}

/**
 * Wizard-mode GitHub connector configuration. Presents a single Repository
 * dropdown sourced from the aggregated `/list-repositories` endpoint and a
 * Reviewer dropdown that re-fetches whenever the repo selection changes
 * (#1133). Bubbles a {@link UnitGitHubConfigRequest} up to the parent
 * wizard.
 */
export function GitHubConnectorWizardStep({
  onChange,
  initialValue,
}: GitHubConnectorWizardStepProps) {
  // Persisted on the binding. The wizard splits the chosen full_name
  // client-side so the wire shape stays `(owner, repo, installationId)`.
  const [owner, setOwner] = useState(initialValue?.owner ?? "");
  const [repo, setRepo] = useState(initialValue?.repo ?? "");
  const [installationId, setInstallationId] = useState<number | null>(
    initialValue?.appInstallationId == null
      ? null
      : Number(initialValue.appInstallationId),
  );
  const [reviewer, setReviewer] = useState(initialValue?.reviewer ?? "");
  // #1127: split webhook-event handling into "use connector defaults" vs.
  // "explicit set". `useDefaults` drives both the wire shape (omit
  // `events` so the server applies its own defaults) AND the UI (the
  // event row becomes informational — checkmarks reflect DEFAULT_EVENTS,
  // boxes are disabled). Initial value:
  //   * no initialValue.events (or empty)  -> defaults checked
  //   * initialValue.events provided        -> defaults unchecked
  // We seed the explicit `events` state with DEFAULT_EVENTS rather than
  // [] so the first click of "uncheck Connector defaults" lands the
  // operator on the same row of marks they were already living with —
  // not an empty form they then have to re-tick.
  const initialUseDefaults =
    initialValue?.events == null || initialValue.events.length === 0;
  const [useDefaults, setUseDefaults] = useState<boolean>(initialUseDefaults);
  const [events, setEvents] = useState<string[]>(
    initialValue?.events && initialValue.events.length > 0
      ? [...initialValue.events]
      : [...DEFAULT_EVENTS],
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
  // When the connector reports `disabled: true` at the deployment level
  // (no GitHub App credentials configured), we hide the install/refresh
  // affordances entirely and render a remediation panel pointing at the
  // CLI / docs. Drives the friendly path for #1186.
  const [disabledReason, setDisabledReason] = useState<string | null>(null);
  // #1153: GitHub OAuth session id (persisted in sessionStorage so it
  // survives the redirect back from GitHub). When null/undefined the
  // wizard renders the "Sign in with GitHub" CTA; when populated it's
  // sent on every list-repositories call so the dropdown is scoped to
  // the signed-in user instead of every repo the App can see across
  // every other user's installations.
  const [oauthSessionId, setOauthSessionId] = useState<string | null>(null);
  const [oauthLogin, setOauthLogin] = useState<string | null>(null);
  const [requiresSignin, setRequiresSignin] = useState(false);
  const [signinPending, setSigninPending] = useState(false);
  const [signinError, setSigninError] = useState<string | null>(null);
  // #1132: tracks an in-flight repositories refetch driven by the
  // Recheck button (or the Refresh affordance on the repository
  // dropdown). The button reads this to disable itself + announce a
  // busy state via `aria-busy`. Distinct from `reposLoading` (which
  // also covers the initial mount fetch) so the Recheck button only
  // shows the "Rechecking…" copy when the user explicitly asks for a
  // refresh.
  const [rechecking, setRechecking] = useState(false);

  // #1132: lifted out of the mount effect so the Recheck button can
  // re-run the fetch without re-mounting the component (and without the
  // monotonic-token gymnastics the previous implementation used). The
  // function is stable across renders because it has no dependencies —
  // all reads come from `api`, all writes go through setState.
  //
  // Note: every setState below happens AFTER an `await`, which is
  // important — it keeps `react-hooks/set-state-in-effect` quiet when
  // the mount effect calls this function (the rule only flags
  // synchronous setState before the first suspension point).
  const fetchRepositories = useCallback(
    async (sessionId: string | null) => {
      let list: GitHubRepositoryResponse[] = [];
      let disabled: string | null = null;
      let needsSignin = false;
      try {
        list = await api.listGitHubRepositories(sessionId ?? undefined);
        setRepositories(list);
        setReposError(null);
        setDisabledReason(null);
        setRequiresSignin(false);
      } catch (err) {
        // disabled-with-reason is a first-class connector state, not a
        // failure (#1186). Render the remediation panel instead of the
        // raw RFC 9110 envelope.
        disabled = extractDisabledReason(err);
        if (disabled !== null) {
          setDisabledReason(disabled);
          setReposError(null);
          setRequiresSignin(false);
        } else if (isRequiresSigninProblem(err)) {
          // #1153: server is telling us the request didn't carry a
          // signed-in GitHub user. Surface the "Sign in with GitHub"
          // affordance instead of leaking the 401 envelope.
          needsSignin = true;
          setRequiresSignin(true);
          setReposError(null);
          setDisabledReason(null);
          // The session id we sent (if any) was rejected — clear it
          // so the next attempt starts fresh.
          if (sessionId !== null) {
            try {
              window.sessionStorage.removeItem(GITHUB_OAUTH_SESSION_KEY);
            } catch {
              // Swallow — sessionStorage may be unavailable in some
              // sandboxed contexts (private mode, SSR). The user can
              // still re-trigger sign-in manually.
            }
            setOauthSessionId(null);
            setOauthLogin(null);
          }
        } else {
          const message = err instanceof Error ? err.message : String(err);
          setReposError(message);
          setDisabledReason(null);
          setRequiresSignin(false);
        }
        setRepositories([]);
      }
      // Fetch the install URL whenever the empty-state banner will show
      // (either the list came back empty, or the call errored). #599: the
      // previous implementation only fetched on the catch branch, so
      // platforms where the App simply has no installations surfaced a
      // banner with no call-to-action link.
      //
      // Skip the install-URL fetch when the connector is disabled — the
      // endpoint will 404 with the same disabled payload, and there is
      // no install URL to render anyway (the deployment hasn't been
      // wired up to a GitHub App yet). Also skip when sign-in is
      // required — the panel we render for that case has its own CTA.
      if (disabled === null && !needsSignin && list.length === 0) {
        try {
          const { url } = await api.getGitHubInstallUrl();
          setInstallUrl(url);
        } catch {
          // Swallow — the banner already tells the user what's wrong.
        }
      }
    },
    [],
  );

  // #1132: imperative wrapper for the Recheck / Refresh buttons. Lifts
  // the `rechecking` flag around the fetch so the UI can render the
  // spinner / aria-busy state without leaking that concern into the
  // mount-time effect.
  const recheckRepositories = useCallback(async () => {
    setRechecking(true);
    try {
      await fetchRepositories(oauthSessionId);
    } finally {
      setRechecking(false);
    }
  }, [fetchRepositories, oauthSessionId]);

  // -- OAuth session bootstrapping (#1153) ----------------------------------
  // On mount, hydrate the OAuth session id from two sources, in order:
  //   1. The URL fragment (`#oauth_session_id=…&login=…`) the OAuth
  //      callback redirected us back to. We strip the fragment so a
  //      page reload doesn't re-process a stale value.
  //   2. `sessionStorage`, which the previous step wrote into so the
  //      session survives across navigations within the wizard.
  // This effect runs exactly once per mount, before the repository
  // fetch, so the first list-repositories call carries the session id
  // when one is available.
  useEffect(() => {
    let resolvedSessionId: string | null = null;
    let resolvedLogin: string | null = null;
    if (typeof window !== "undefined") {
      const hash = window.location.hash;
      if (hash.length > 1) {
        const fragmentParams = new URLSearchParams(hash.slice(1));
        const fragmentSession = fragmentParams.get("oauth_session_id");
        const fragmentLogin = fragmentParams.get("login");
        if (fragmentSession) {
          resolvedSessionId = fragmentSession;
          resolvedLogin = fragmentLogin;
          try {
            window.sessionStorage.setItem(
              GITHUB_OAUTH_SESSION_KEY,
              fragmentSession,
            );
          } catch {
            // Best-effort persistence.
          }
          // Strip the fragment without triggering a navigation so a
          // refresh won't replay a stale session id.
          const cleanUrl =
            window.location.pathname + window.location.search;
          try {
            window.history.replaceState(null, "", cleanUrl);
          } catch {
            // Some browsers (very old) reject this; non-fatal.
          }
        }
      }
      if (resolvedSessionId === null) {
        try {
          resolvedSessionId =
            window.sessionStorage.getItem(GITHUB_OAUTH_SESSION_KEY);
        } catch {
          resolvedSessionId = null;
        }
      }
    }
    setOauthSessionId(resolvedSessionId);
    setOauthLogin(resolvedLogin);
  }, []);

  // -- Repositories (mount fetch) -------------------------------------------
  // Re-runs whenever the OAuth session id changes (initial hydration,
  // post-callback set, server-rejected clear) so the dropdown stays in
  // lockstep with the signed-in identity.
  useEffect(() => {
    let cancelled = false;
    setReposLoading(true);
    (async () => {
      try {
        await fetchRepositories(oauthSessionId);
      } finally {
        if (!cancelled) setReposLoading(false);
      }
    })();
    return () => {
      cancelled = true;
    };
  }, [fetchRepositories, oauthSessionId]);

  // -- Sign-in handler (#1153) ----------------------------------------------
  // Calls the connector's authorize endpoint with the current portal
  // path as `clientState`, then full-page navigates to GitHub. After
  // GitHub redirects back through the connector callback, the user
  // lands here again with `#oauth_session_id=…&login=…` in the URL
  // fragment, which the bootstrap effect picks up and persists.
  const beginSignin = useCallback(async () => {
    if (typeof window === "undefined") return;
    setSigninPending(true);
    setSigninError(null);
    try {
      const returnPath = window.location.pathname + window.location.search;
      const { authorizeUrl } = await api.beginGitHubOAuth(returnPath);
      window.location.href = authorizeUrl;
    } catch (err) {
      const message = err instanceof Error ? err.message : String(err);
      setSigninError(message);
      setSigninPending(false);
    }
  }, []);

  // -- Sign-out handler (#1153) ---------------------------------------------
  // Clears the session locally so the wizard reverts to the "Sign in
  // with GitHub" CTA. We deliberately do NOT call /oauth/revoke here —
  // the user may have other browser tabs / portal sessions using the
  // same OAuth session, and revoking the token would tear those down
  // too. The platform-wide sign-out flow owns remote revocation.
  const clearSignin = useCallback(() => {
    if (typeof window !== "undefined") {
      try {
        window.sessionStorage.removeItem(GITHUB_OAUTH_SESSION_KEY);
      } catch {
        // Best-effort.
      }
    }
    setOauthSessionId(null);
    setOauthLogin(null);
    setRequiresSignin(true);
  }, []);

  // -- Collaborators (re-fetched whenever the repo selection changes) -------
  useEffect(() => {
    if (
      installationId == null ||
      owner.trim() === "" ||
      repo.trim() === ""
    ) {
      // No repo chosen yet — clear stale state so the dropdown collapses.
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

  // -- Bubble validated state up to the wizard ------------------------------
  // Null when the minimum required field (a chosen repository) is missing
  // so the wizard knows not to bundle a partially-filled config.
  useEffect(() => {
    const trimmedOwner = owner.trim();
    const trimmedRepo = repo.trim();
    if (!trimmedOwner || !trimmedRepo || installationId == null) {
      onChange(null);
      return;
    }
    onChange({
      owner: trimmedOwner,
      repo: trimmedRepo,
      appInstallationId: installationId,
      // #1127: omit `events` whenever the operator picked "Connector
      // defaults" so the server resolves the set itself. When they
      // picked an explicit list we forward it verbatim — the server
      // still falls back to its defaults if we somehow send an empty
      // list, but bubbling the explicit selection is the user's
      // intent of record either way.
      events: useDefaults
        ? undefined
        : events.length > 0
          ? events
          : undefined,
      reviewer: reviewer.trim() === "" ? undefined : reviewer.trim(),
    });
  }, [owner, repo, installationId, events, reviewer, useDefaults, onChange]);

  const toggleEvent = (e: string) => {
    setEvents((prev) =>
      prev.includes(e) ? prev.filter((x) => x !== e) : [...prev, e],
    );
  };

  // The dropdown's value is the full_name; we split client-side so the
  // wire shape stays `(owner, repo, installationId)`. Selecting "" clears
  // the selection.
  const selectedFullName =
    owner !== "" && repo !== "" ? `${owner}/${repo}` : "";

  const handleRepoChange = (next: string) => {
    if (next === "") {
      setOwner("");
      setRepo("");
      setInstallationId(null);
      setReviewer("");
      return;
    }
    const match = repositories?.find((r) => r.fullName === next) ?? null;
    if (match === null) return;
    setOwner(match.owner);
    setRepo(match.repo);
    setInstallationId(Number(match.installationId));
    // Selecting a different repo invalidates the previously chosen
    // reviewer — collaborators are repo-scoped.
    setReviewer("");
  };

  // Combined busy flag for the dropdown + Refresh button. Both the
  // initial mount fetch (`reposLoading`) and any subsequent recheck
  // (`rechecking`) should disable the dropdown / spin the icon.
  const repoBusy = reposLoading || rechecking;

  return (
    <div className="space-y-4 rounded-md border border-border bg-muted/30 p-4">
      <div className="flex items-center gap-2">
        <Github className="h-4 w-4" />
        <span className="text-sm font-medium">GitHub connector</span>
      </div>

      {disabledReason === null && oauthLogin !== null && !requiresSignin && (
        <div
          className="flex items-center justify-between gap-2 rounded-md border border-border bg-background px-3 py-2 text-xs"
          data-testid="github-signed-in-as"
        >
          <span className="text-muted-foreground">
            Showing repositories for{" "}
            <span className="font-medium text-foreground">
              @{oauthLogin}
            </span>
            .
          </span>
          <button
            type="button"
            className="text-xs text-muted-foreground underline-offset-2 hover:underline focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring focus-visible:ring-offset-2 focus-visible:ring-offset-background"
            onClick={clearSignin}
            data-testid="github-signout"
          >
            Sign out
          </button>
        </div>
      )}

      {disabledReason === null && requiresSignin && (
        <div
          role="alert"
          className="rounded-md border border-info/50 bg-info/15 px-3 py-2 text-sm text-info"
          data-testid="github-signin-required"
        >
          <p className="font-medium">Sign in with GitHub.</p>
          <p className="mt-1 text-foreground">
            We only show the repositories your GitHub account can see and
            on which the configured GitHub App is installed. Sign in to
            populate the dropdown.
          </p>
          <div className="mt-2 flex flex-wrap items-center gap-2">
            <Button
              size="sm"
              variant="outline"
              onClick={() => void beginSignin()}
              disabled={signinPending}
              aria-busy={signinPending}
              data-testid="github-signin"
            >
              {signinPending ? (
                <Loader2
                  className="mr-1 h-4 w-4 animate-spin"
                  aria-hidden="true"
                />
              ) : (
                <Github className="mr-1 h-4 w-4" aria-hidden="true" />
              )}
              {signinPending ? "Redirecting…" : "Sign in with GitHub"}
            </Button>
          </div>
          {signinError && (
            <p className="mt-2 text-xs text-destructive">
              Could not start sign-in: {signinError}
            </p>
          )}
        </div>
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

      {disabledReason === null &&
        !requiresSignin &&
        repositories &&
        repositories.length === 0 && (
          <div
            role="alert"
            className="rounded-md border border-warning/50 bg-warning/15 px-3 py-2 text-sm text-warning"
          >
            <p className="font-medium">No GitHub repositories visible.</p>
            <p className="mt-1 text-foreground">
              Install the GitHub App on your account or organisation, and
              grant it access to at least one repository, before binding this
              unit.
            </p>
            {/* #1132: Install-app routes the operator off-site to GitHub
                — once they install the App, GitHub redirects them away,
                and they then have to manually return to the wizard. The
                old code never re-fetched, so the panel stayed stuck on
                "No installations" and the operator hit a dead end. The
                Recheck button re-runs the same `list-repositories`
                fetch in place; it's announced as `aria-busy` while in
                flight and disabled to avoid double-clicks. */}
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

      {disabledReason === null && !requiresSignin && (
        <label className="block space-y-1">
          <span className="text-xs text-muted-foreground">
            Repository<span className="text-destructive"> *</span>
          </span>
          <div className="flex items-center gap-2">
            <select
              aria-label="Repository"
              className="h-9 flex-1 rounded-md border border-input bg-background px-3 text-sm"
              value={selectedFullName}
              onChange={(e) => handleRepoChange(e.target.value)}
              disabled={repoBusy || (repositories?.length ?? 0) === 0}
            >
              <option value="">
                {repoBusy
                  ? "Loading repositories…"
                  : (repositories?.length ?? 0) === 0
                    ? "No repositories available"
                    : "Select a repository…"}
              </option>
              {repositories?.map((r) => (
                <option key={`${r.installationId}:${r.repositoryId}`} value={r.fullName}>
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
          {selectedFullName !== "" && (
            <span className="block text-[11px] text-muted-foreground">
              {repositories?.find((r) => r.fullName === selectedFullName)
                ?.private && (
                <span className="inline-flex items-center gap-1">
                  <Lock className="h-3 w-3" aria-hidden="true" />
                  Private repository.{" "}
                </span>
              )}
              The GitHub App installation covering this repo will be used.
            </span>
          )}
        </label>
      )}

      {disabledReason === null &&
        !requiresSignin &&
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
            Requested as the reviewer when this unit&apos;s agents open pull
            requests. Optional — agents that pass a reviewer explicitly still
            override per-call.
          </span>
        </label>
      )}

      {/* #1127: webhook event selection. The "Connector defaults" toggle
          is the primary control. While it's checked the per-event row
          becomes purely informational — checkmarks reflect what the
          server would apply (DEFAULT_EVENTS) and the inputs are
          disabled. Unchecking it pre-populates the explicit list with
          the same defaults so the operator starts from "what was
          already happening", not an empty form. */}
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
