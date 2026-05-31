"use client";

// One-page Slack install wizard (#2882, ADR-0061 §2.5).
//
// Eliminates the CLI round-trip from the common-case Slack install: the
// operator pastes a Slack Configuration Token, clicks Install, consents in
// the OAuth popup, and is done — no terminal, no `spring connector slack
// install`. The server-side endpoint
// (`POST /api/v1/tenant/connectors/slack/install`) drives Slack's Manifest
// API, persists the four OAuth credentials as tenant secrets, and returns
// a state-bearing consent URL. This page renders the form, the manifest
// preview (the endpoint's dry-run), and drives the OAuth popup with the
// same `awaitSlackOAuthHandoff` helper the settings panel uses.
//
// Connect-now shortcut: when this tenant already has a complete set of
// OAuth credentials (e.g. a prior `spring connector slack install` that
// never finished the consent step), `GET .../install/status` reports it
// and the wizard offers a "connect now" card that skips registration and
// jumps straight to OAuth consent via `beginSlackOAuthAuthorize`.
//
// The `spring connector slack install` CLI verb stays for headless / CI /
// air-gapped installs; a single shared manifest builder
// (`Cvoya.Spring.Connector.Slack.Provisioning`) keeps the two surfaces in
// lock-step. Socket Mode local-dev still needs a manually-generated
// app-level token + `spring connector slack forward` in v0.1 — the
// server-side bridge is tracked in #2883.

import { useCallback, useEffect, useRef, useState } from "react";
import Link from "next/link";
import { useRouter } from "next/navigation";
import { ArrowLeft, CheckCircle2, ExternalLink, Loader2, Slack } from "lucide-react";

import { Button } from "@/components/ui/button";
import { Card, CardContent, CardHeader, CardTitle } from "@/components/ui/card";
import { Input } from "@/components/ui/input";
import { useToast } from "@/components/ui/toast";
import { ApiError, api } from "@/lib/api/client";
import { formatTranslatedError } from "@/lib/api/translate-error";
import {
  awaitSlackOAuthHandoff,
  buildSlackOAuthClientState,
  type SlackOAuthHandoffOutcome,
} from "@connector-slack/slack-oauth-browser";

const SLACK_DOCS_URL =
  "https://github.com/cvoya-com/spring-voyage/blob/main/docs/decisions/0061-slack-connector-oss-shape.md";
const SLACK_APPS_URL = "https://api.slack.com/apps";
const DEFAULT_APP_NAME = "Spring Voyage";

type WizardStatus =
  | "idle"
  | "previewing"
  | "installing"
  | "connecting"
  | "awaiting"
  | "failed";

interface WizardError {
  title: string;
  detail: string;
  /** True when the failure is an expired / rejected configuration token. */
  tokenRejected?: boolean;
}

const TOKEN_REJECTED_CODES = new Set([
  "invalid_auth",
  "not_authed",
  "token_expired",
  "token_revoked",
  "account_inactive",
]);

function describeError(err: unknown): WizardError {
  if (err instanceof ApiError && err.problem) {
    const code = typeof err.problem.code === "string" ? err.problem.code : undefined;
    const detail =
      typeof err.problem.detail === "string" ? err.problem.detail : undefined;
    const title =
      typeof err.problem.title === "string" ? err.problem.title : undefined;
    if (code !== undefined && TOKEN_REJECTED_CODES.has(code)) {
      return {
        title: "Slack rejected the configuration token",
        detail:
          detail ??
          "Configuration Tokens are short-lived (~12h). Generate a fresh one from your workspace admin's 'Your Apps' page and paste it again.",
        tokenRejected: true,
      };
    }
    if (title !== undefined || detail !== undefined) {
      return {
        title: title ?? "Slack install failed",
        detail: detail ?? formatTranslatedError(err),
      };
    }
  }
  return { title: "Slack install failed", detail: formatTranslatedError(err) };
}

function prettyManifest(manifestJson: string): string {
  try {
    return JSON.stringify(JSON.parse(manifestJson), null, 2);
  } catch {
    return manifestJson;
  }
}

export default function SlackInstallWizardPage() {
  const router = useRouter();
  const { toast } = useToast();

  const [configToken, setConfigToken] = useState("");
  const [appName, setAppName] = useState(DEFAULT_APP_NAME);
  const [svHost, setSvHost] = useState("");
  const [socketMode, setSocketMode] = useState(false);

  const [status, setStatus] = useState<WizardStatus>("idle");
  const [error, setError] = useState<WizardError | null>(null);
  const [manifestPreview, setManifestPreview] = useState<string | null>(null);

  // null = still checking / unknown. true surfaces the "connect now"
  // shortcut. A failed status check defaults to false (form-only).
  const [oauthConfigured, setOauthConfigured] = useState<boolean | null>(null);

  const popupRef = useRef<Window | null>(null);
  const abortRef = useRef<AbortController | null>(null);

  // Detect whether OAuth credentials already resolve for this tenant so we
  // can offer the connect-now shortcut.
  useEffect(() => {
    let cancelled = false;
    api
      .getSlackInstallStatus()
      .then((res) => {
        if (!cancelled) setOauthConfigured(res.oauthConfigured);
      })
      .catch(() => {
        if (!cancelled) setOauthConfigured(false);
      });
    return () => {
      cancelled = true;
    };
  }, []);

  // Abort the OAuth-handoff listener if the operator navigates away
  // mid-install so it doesn't outlive the page.
  useEffect(() => () => abortRef.current?.abort(), []);

  const busy =
    status === "previewing" ||
    status === "installing" ||
    status === "connecting" ||
    status === "awaiting";

  const buildBody = useCallback(
    (dryRun: boolean) => ({
      configToken: configToken.trim() === "" ? null : configToken.trim(),
      appName: appName.trim() === "" ? null : appName.trim(),
      svHost: svHost.trim() === "" ? null : svHost.trim(),
      socketMode,
      dryRun,
      clientState: dryRun ? null : buildSlackOAuthClientState(),
    }),
    [configToken, appName, svHost, socketMode],
  );

  /**
   * Shared OAuth-consent driver for both the install and connect-now
   * flows. Opens the popup synchronously (browsers block `window.open`
   * after an `await`), resolves the authorize URL via `fetchAuthorizeUrl`,
   * navigates the popup, then resolves on the postMessage handoff.
   */
  const runOAuthConsent = useCallback(
    async (
      openingStatus: "installing" | "connecting",
      fetchAuthorizeUrl: () => Promise<string | null>,
    ) => {
      const popup = window.open(
        "about:blank",
        "spring-voyage-slack-oauth",
        "popup,width=720,height=820",
      );
      if (popup === null) {
        setError({
          title: "Your browser blocked the Slack authorization window",
          detail: "Allow popups for this site and try again.",
        });
        setStatus("failed");
        return;
      }
      popupRef.current = popup;
      setStatus(openingStatus);
      setError(null);

      let authorizeUrl: string | null;
      try {
        authorizeUrl = await fetchAuthorizeUrl();
      } catch (err) {
        popup.close();
        popupRef.current = null;
        setError(describeError(err));
        setStatus("failed");
        return;
      }

      if (authorizeUrl === null || authorizeUrl === "") {
        popup.close();
        popupRef.current = null;
        setError({
          title: "Slack returned no consent URL",
          detail:
            "The credentials were saved. Go to Settings → Slack workspace and click Reconnect to finish the consent step.",
        });
        setStatus("failed");
        return;
      }

      popup.location.href = authorizeUrl;
      popup.focus();
      setStatus("awaiting");

      const abortController = new AbortController();
      abortRef.current?.abort();
      abortRef.current = abortController;

      const outcome: SlackOAuthHandoffOutcome = await awaitSlackOAuthHandoff({
        popup,
        signal: abortController.signal,
      });

      try {
        popup.close();
      } catch {
        // The callback HTML closes the popup itself; a second close is a
        // no-op when the window is already gone.
      }
      popupRef.current = null;
      abortRef.current = null;

      if (outcome.kind === "success") {
        toast({
          title: "Slack workspace connected",
          description:
            "The bot can now post DMs for this tenant.",
        });
        router.push("/settings");
        return;
      }

      if (outcome.kind === "error") {
        setError({
          title: "Slack install didn't complete",
          detail:
            outcome.message || `Slack returned error code '${outcome.error}'.`,
        });
        setStatus("failed");
        return;
      }

      // Client-side observations: popup closed, deadline elapsed, or aborted.
      setError({
        title: "Slack install didn't complete",
        detail:
          outcome.kind === "popup-closed"
            ? "The Slack window closed before consent finished. Check Settings → Slack workspace, or try again."
            : outcome.kind === "timed-out"
              ? "The Slack window stayed open too long. Check Settings → Slack workspace, or try again."
              : "The install was interrupted. Check Settings → Slack workspace, or try again.",
      });
      setStatus("failed");
    },
    [router, toast],
  );

  const preview = useCallback(async () => {
    setStatus("previewing");
    setError(null);
    try {
      const res = await api.installSlackApp(buildBody(true));
      setManifestPreview(prettyManifest(res.manifestJson));
      setStatus("idle");
    } catch (err) {
      setError(describeError(err));
      setStatus("failed");
    }
  }, [buildBody]);

  const install = useCallback(async () => {
    if (configToken.trim() === "") {
      setError({
        title: "Configuration token required",
        detail:
          "Paste a Slack Configuration Token before installing. Generate one from your workspace admin's 'Your Apps' page.",
        tokenRejected: true,
      });
      setStatus("failed");
      return;
    }

    await runOAuthConsent("installing", async () => {
      const res = await api.installSlackApp(buildBody(false));
      // Surface the manifest that was actually created so the operator
      // can see what got registered even after a successful create.
      setManifestPreview(prettyManifest(res.manifestJson));
      return res.authorizeUrl ?? null;
    });
  }, [buildBody, configToken, runOAuthConsent]);

  const connectExisting = useCallback(async () => {
    await runOAuthConsent("connecting", async () => {
      const res = await api.beginSlackOAuthAuthorize({
        clientState: buildSlackOAuthClientState(),
      });
      return res.authorizeUrl;
    });
  }, [runOAuthConsent]);

  return (
    <div className="mx-auto max-w-3xl space-y-6" data-testid="slack-install-wizard">
      <div className="space-y-1">
        <Link
          href="/settings"
          className="inline-flex items-center gap-1 text-sm text-muted-foreground hover:text-foreground"
        >
          <ArrowLeft className="h-3.5 w-3.5" aria-hidden="true" />
          Back to settings
        </Link>
        <div className="flex items-center gap-2">
          <Slack className="h-5 w-5 text-primary" aria-hidden="true" />
          <h1 className="text-2xl font-bold">Install Slack workspace</h1>
        </div>
        <p className="text-sm text-muted-foreground">
          Register a Slack app for this deployment without touching a
          terminal. Paste a Configuration Token, review the manifest, then
          install and consent in the popup. The CLI equivalent is{" "}
          <code className="rounded bg-muted px-1 py-0.5 font-mono text-xs">
            spring connector slack install
          </code>
          .
        </p>
      </div>

      {error !== null && (
        <div
          role="alert"
          className="space-y-1 rounded-md border border-destructive/50 bg-destructive/10 px-3 py-2 text-sm text-destructive"
          data-testid="slack-install-error"
        >
          <p className="font-medium">{error.title}</p>
          <p className="text-xs text-foreground">{error.detail}</p>
        </div>
      )}

      {oauthConfigured === true && (
        <Card data-testid="slack-install-connect-card">
          <CardHeader>
            <CardTitle className="flex items-center gap-2 text-base">
              <CheckCircle2 className="h-4 w-4 text-success" aria-hidden="true" />
              Credentials already configured
            </CardTitle>
          </CardHeader>
          <CardContent className="space-y-3">
            <p className="text-sm text-muted-foreground">
              This deployment already has Slack OAuth credentials (e.g. from a
              prior{" "}
              <code className="rounded bg-muted px-1 py-0.5 font-mono text-xs">
                spring connector slack install
              </code>
              ). Skip app registration and connect the workspace directly.
            </p>
            <Button
              variant="default"
              onClick={connectExisting}
              disabled={busy}
              aria-busy={status === "connecting" || status === "awaiting"}
              data-testid="slack-install-connect-existing"
            >
              {status === "connecting" || status === "awaiting" ? (
                <Loader2 className="mr-1 h-4 w-4 animate-spin" aria-hidden="true" />
              ) : (
                <Slack className="mr-1 h-4 w-4" aria-hidden="true" />
              )}
              {status === "connecting"
                ? "Opening Slack…"
                : status === "awaiting"
                  ? "Waiting for Slack…"
                  : "Connect to Slack"}
            </Button>
          </CardContent>
        </Card>
      )}

      {oauthConfigured === true && (
        <p className="text-sm font-medium text-muted-foreground">
          Or register a new Slack app:
        </p>
      )}

      <Card>
        <CardHeader>
          <CardTitle className="text-base">Slack app details</CardTitle>
        </CardHeader>
        <CardContent className="space-y-4">
          <div className="space-y-1.5">
            <label
              htmlFor="slack-config-token"
              className="text-sm font-medium"
            >
              Configuration token
            </label>
            <Input
              id="slack-config-token"
              data-testid="slack-install-config-token"
              type="password"
              autoComplete="off"
              spellCheck={false}
              placeholder="xoxe.xoxp-…"
              value={configToken}
              onChange={(e) => setConfigToken(e.target.value)}
              disabled={busy}
            />
            <p className="text-xs text-muted-foreground">
              Generate one from your workspace admin&apos;s{" "}
              <a
                href={SLACK_APPS_URL}
                target="_blank"
                rel="noopener noreferrer"
                className="inline-flex items-center gap-1 text-primary hover:underline"
              >
                Your Apps
                <ExternalLink className="h-3 w-3" aria-hidden="true" />
              </a>{" "}
              page → &quot;Generate Configuration Tokens&quot;. They expire
              after ~12 hours; Slack only issues them to humans, so this step
              can&apos;t be automated.
            </p>
          </div>

          <div className="space-y-1.5">
            <label htmlFor="slack-app-name" className="text-sm font-medium">
              App name
            </label>
            <Input
              id="slack-app-name"
              data-testid="slack-install-app-name"
              type="text"
              placeholder={DEFAULT_APP_NAME}
              value={appName}
              onChange={(e) => setAppName(e.target.value)}
              disabled={busy}
            />
            <p className="text-xs text-muted-foreground">
              The display name for the new Slack app. Must be unique within
              the workspace.
            </p>
          </div>

          <div className="space-y-1.5">
            <label htmlFor="slack-sv-host" className="text-sm font-medium">
              Spring Voyage host{" "}
              <span className="font-normal text-muted-foreground">
                (optional)
              </span>
            </label>
            <Input
              id="slack-sv-host"
              data-testid="slack-install-sv-host"
              type="url"
              placeholder="Leave blank to use this deployment's URL"
              value={svHost}
              onChange={(e) => setSvHost(e.target.value)}
              disabled={busy}
            />
            <p className="text-xs text-muted-foreground">
              The public base URL Slack will call for OAuth, events, and slash
              commands. Defaults to the URL this portal is served from.
            </p>
          </div>

          <label className="flex items-start gap-2 text-sm">
            <input
              type="checkbox"
              data-testid="slack-install-socket-mode"
              className="mt-0.5 h-4 w-4 rounded border-input"
              checked={socketMode}
              onChange={(e) => setSocketMode(e.target.checked)}
              disabled={busy}
            />
            <span>
              <span className="font-medium">Enable Socket Mode</span>
              <span className="block text-xs text-muted-foreground">
                For local-dev installs with no public URL. You&apos;ll still
                generate an app-level token and run{" "}
                <code className="rounded bg-muted px-1 py-0.5 font-mono text-[11px]">
                  spring connector slack forward
                </code>{" "}
                in a second terminal (a server-side bridge is planned —{" "}
                <a
                  href="https://github.com/cvoya-com/spring-voyage/issues/2883"
                  target="_blank"
                  rel="noopener noreferrer"
                  className="text-primary hover:underline"
                >
                  #2883
                </a>
                ).
              </span>
            </span>
          </label>
        </CardContent>
      </Card>

      <Card>
        <CardHeader>
          <CardTitle className="text-base">Review &amp; install</CardTitle>
        </CardHeader>
        <CardContent className="space-y-4">
          <div className="flex flex-wrap items-center gap-3">
            <Button
              variant="outline"
              onClick={preview}
              disabled={busy}
              aria-busy={status === "previewing"}
              data-testid="slack-install-preview"
            >
              {status === "previewing" ? (
                <Loader2 className="mr-1 h-4 w-4 animate-spin" aria-hidden="true" />
              ) : null}
              Preview manifest
            </Button>
            <Button
              variant="default"
              onClick={install}
              disabled={busy}
              aria-busy={status === "installing" || status === "awaiting"}
              data-testid="slack-install-submit"
            >
              {status === "installing" || status === "awaiting" ? (
                <Loader2 className="mr-1 h-4 w-4 animate-spin" aria-hidden="true" />
              ) : (
                <Slack className="mr-1 h-4 w-4" aria-hidden="true" />
              )}
              {status === "installing"
                ? "Creating Slack app…"
                : status === "awaiting"
                  ? "Waiting for Slack…"
                  : "Install in Slack"}
            </Button>
            {status === "awaiting" && (
              <span className="text-xs text-muted-foreground">
                Complete the consent in the Slack window. You&apos;ll return to
                settings when it&apos;s done.
              </span>
            )}
          </div>

          {manifestPreview !== null && (
            <div className="space-y-1.5">
              <p className="text-xs font-medium uppercase tracking-wide text-muted-foreground">
                Manifest preview
              </p>
              <pre
                data-testid="slack-install-manifest-preview"
                className="max-h-80 overflow-auto rounded-md border border-border bg-muted/40 p-3 text-xs"
              >
                {manifestPreview}
              </pre>
            </div>
          )}

          <p className="text-xs text-muted-foreground">
            Spring Voyage requests a fixed set of bot scopes and slash
            commands — see{" "}
            <a
              href={SLACK_DOCS_URL}
              target="_blank"
              rel="noopener noreferrer"
              className="inline-flex items-center gap-1 text-primary hover:underline"
            >
              the documentation
              <ExternalLink className="h-3 w-3" aria-hidden="true" />
            </a>{" "}
            for the rationale and v0.1 limitations.
          </p>
        </CardContent>
      </Card>
    </div>
  );
}
