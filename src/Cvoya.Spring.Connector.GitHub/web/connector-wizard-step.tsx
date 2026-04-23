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

import { useEffect, useState } from "react";
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
  const [events, setEvents] = useState<string[]>(
    initialValue?.events ? [...initialValue.events] : [],
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
  // Incremented by the Refresh button to re-run the repositories fetch
  // effect. Using a monotonically-increasing token keeps the fetch logic
  // inside the effect (so `setState` after the `await` resolves — which
  // doesn't count as "synchronous setState inside an effect") while still
  // supporting imperative refresh from the UI.
  const [refreshToken, setRefreshToken] = useState(0);

  // -- Repositories ---------------------------------------------------------
  useEffect(() => {
    let cancelled = false;
    setReposLoading(true);
    (async () => {
      let list: GitHubRepositoryResponse[] = [];
      let disabled: string | null = null;
      try {
        list = await api.listGitHubRepositories();
        if (cancelled) return;
        setRepositories(list);
        setReposError(null);
        setDisabledReason(null);
      } catch (err) {
        if (cancelled) return;
        // disabled-with-reason is a first-class connector state, not a
        // failure (#1186). Render the remediation panel instead of the
        // raw RFC 9110 envelope.
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
        if (!cancelled) setReposLoading(false);
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
      // wired up to a GitHub App yet).
      if (cancelled || disabled !== null) return;
      if (list.length === 0) {
        try {
          const { url } = await api.getGitHubInstallUrl();
          if (cancelled) return;
          setInstallUrl(url);
        } catch {
          // Swallow — the banner already tells the user what's wrong.
        }
      }
    })();
    return () => {
      cancelled = true;
    };
  }, [refreshToken]);

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
      events: events.length > 0 ? events : undefined,
      reviewer: reviewer.trim() === "" ? undefined : reviewer.trim(),
    });
  }, [owner, repo, installationId, events, reviewer, onChange]);

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

      {disabledReason === null &&
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
            {reposError && (
              <p className="mt-2 text-xs text-muted-foreground">
                ({reposError})
              </p>
            )}
          </div>
        )}

      {disabledReason === null && (
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
              disabled={reposLoading || (repositories?.length ?? 0) === 0}
            >
              <option value="">
                {reposLoading
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
              onClick={() => setRefreshToken((n) => n + 1)}
              aria-label="Refresh repositories"
              disabled={reposLoading}
            >
              {reposLoading ? (
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

      {disabledReason === null && installationId != null && (
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

      <div className="space-y-1">
        <span className="text-xs text-muted-foreground">Webhook events</span>
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
        <span className="block text-[11px] text-muted-foreground">
          Leave empty to use the connector&apos;s default event set.
        </span>
      </div>
    </div>
  );
}
