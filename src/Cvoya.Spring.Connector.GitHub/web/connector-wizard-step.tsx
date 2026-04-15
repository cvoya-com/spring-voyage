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

import { useEffect, useState } from "react";
import { Github, RefreshCw } from "lucide-react";

import { Button } from "@/components/ui/button";
import { Input } from "@/components/ui/input";
import { api } from "@/lib/api/client";
import type {
  GitHubInstallationResponse,
  UnitGitHubConfigRequest,
} from "@/lib/api/types";

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
 * Wizard-mode GitHub connector configuration. Collects owner / repo /
 * installation / events, validates locally, and bubbles a
 * {@link UnitGitHubConfigRequest} up to the parent wizard.
 */
export function GitHubConnectorWizardStep({
  onChange,
  initialValue,
}: GitHubConnectorWizardStepProps) {
  const [owner, setOwner] = useState(initialValue?.owner ?? "");
  const [repo, setRepo] = useState(initialValue?.repo ?? "");
  const [installationId, setInstallationId] = useState<number | null>(
    initialValue?.appInstallationId == null
      ? null
      : Number(initialValue.appInstallationId),
  );
  const [events, setEvents] = useState<string[]>(
    initialValue?.events ? [...initialValue.events] : [],
  );

  const [installations, setInstallations] = useState<
    GitHubInstallationResponse[] | null
  >(null);
  const [installationsError, setInstallationsError] = useState<string | null>(
    null,
  );
  const [installUrl, setInstallUrl] = useState<string | null>(null);
  // Incremented by the Refresh button to re-run the installations fetch
  // effect. Using a monotonically-increasing token keeps the fetch logic
  // inside the effect (so `setState` after the `await` resolves — which
  // doesn't count as "synchronous setState inside an effect") while still
  // supporting imperative refresh from the UI.
  const [refreshToken, setRefreshToken] = useState(0);

  useEffect(() => {
    let cancelled = false;
    (async () => {
      try {
        const list = await api.listGitHubInstallations();
        if (cancelled) return;
        setInstallations(list);
        setInstallationsError(null);
      } catch (err) {
        if (cancelled) return;
        const message = err instanceof Error ? err.message : String(err);
        setInstallationsError(message);
        setInstallations([]);
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

  // Push validated state up to the wizard on every change. Null when the
  // minimum required fields are missing so the wizard knows not to bundle
  // a partially-filled config.
  useEffect(() => {
    const trimmedOwner = owner.trim();
    const trimmedRepo = repo.trim();
    if (!trimmedOwner || !trimmedRepo) {
      onChange(null);
      return;
    }
    onChange({
      owner: trimmedOwner,
      repo: trimmedRepo,
      appInstallationId: installationId ?? undefined,
      events: events.length > 0 ? events : undefined,
    });
  }, [owner, repo, installationId, events, onChange]);

  const toggleEvent = (e: string) => {
    setEvents((prev) =>
      prev.includes(e) ? prev.filter((x) => x !== e) : [...prev, e],
    );
  };

  return (
    <div className="space-y-4 rounded-md border border-border bg-muted/30 p-4">
      <div className="flex items-center gap-2">
        <Github className="h-4 w-4" />
        <span className="text-sm font-medium">GitHub connector</span>
      </div>

      {installations && installations.length === 0 && (
        <div className="rounded-md border border-amber-500/50 bg-amber-500/10 px-3 py-2 text-sm text-amber-900 dark:text-amber-200">
          <p className="font-medium">No GitHub App installations found.</p>
          <p className="mt-1">
            Install the GitHub App on your account or organisation before
            binding this unit.
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
            <p className="mt-1 text-xs opacity-80">({installationsError})</p>
          )}
        </div>
      )}

      <div className="grid grid-cols-1 gap-3 sm:grid-cols-2">
        <label className="block space-y-1">
          <span className="text-xs text-muted-foreground">
            Repository owner<span className="text-destructive"> *</span>
          </span>
          <Input
            value={owner}
            onChange={(e) => setOwner(e.target.value)}
            placeholder="acme"
            autoComplete="off"
          />
        </label>
        <label className="block space-y-1">
          <span className="text-xs text-muted-foreground">
            Repository name<span className="text-destructive"> *</span>
          </span>
          <Input
            value={repo}
            onChange={(e) => setRepo(e.target.value)}
            placeholder="platform"
            autoComplete="off"
          />
        </label>
      </div>

      {installations && installations.length > 0 && (
        <label className="block space-y-1">
          <span className="text-xs text-muted-foreground">
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
              onClick={() => setRefreshToken((n) => n + 1)}
              aria-label="Refresh installations"
            >
              <RefreshCw className="h-4 w-4" />
            </Button>
          </div>
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
