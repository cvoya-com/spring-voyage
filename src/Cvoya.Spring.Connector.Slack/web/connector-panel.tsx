"use client";

// Slack connector panel (issue #2820, ADR-0061).
//
// Lives inside the connector package (`src/Cvoya.Spring.Connector.Slack/
// web/`) mirroring the GitHub layout — the .NET connector owns both
// its server-side code and its web surface. The host web app pulls
// this in via the `@connector-slack/*` tsconfig path alias and the
// `defaults.tsx` drawer-panel registry.
//
// Unlike the GitHub connector which binds per-unit, Slack binds at
// TENANT scope (ADR-0061 §1). The portal surface is therefore a single
// settings panel ("Slack workspace") rendered on `/settings` rather
// than a tab inside the Explorer's unit Connector tab.
//
// v0.1 restrictions (ADR-0061 §2):
//   - Single bound user (the OAuth installer) — surfaced verbatim.
//   - DM-only operation in v0.1 (no channels) — caveat copy below.
//   - Enterprise Grid refused at install time (HTTP 422 with
//     `code = SlackEnterpriseGridUnsupported`) — surfaced with an
//     actionable message instead of "unknown error".
//   - One workspace per OSS tenant.

import { useCallback, useEffect, useMemo, useRef, useState } from "react";
import { useQueryClient } from "@tanstack/react-query";
import {
  AlertTriangle,
  CheckCircle2,
  ExternalLink,
  Hash,
  Loader2,
  MessageSquare,
  RefreshCw,
  Slack,
  Unplug,
  User,
} from "lucide-react";

import { Badge } from "@/components/ui/badge";
import { Button } from "@/components/ui/button";
import { ConfirmDialog } from "@/components/ui/confirm-dialog";
import { Skeleton } from "@/components/ui/skeleton";
import { useToast } from "@/components/ui/toast";
import { ApiError, api } from "@/lib/api/client";
import { useTenantSlackBinding } from "@/lib/api/queries";
import { queryKeys } from "@/lib/api/query-keys";
import { formatTranslatedError } from "@/lib/api/translate-error";
import type { TenantConnectorBindingResponse } from "@/lib/api/types";

import {
  awaitSlackOAuthHandoff,
  buildSlackOAuthClientState,
  SLACK_OAUTH_HANDOFF_TIMEOUT_MS,
  type SlackOAuthHandoffOutcome,
} from "./slack-oauth-browser";

/**
 * The opaque connector `config` payload mirrors the C# `TenantSlackConfig`
 * record (see `src/Cvoya.Spring.Connector.Slack/TenantSlackConfig.cs`).
 * The portal speaks the wire shape directly — the JSON-property names
 * are stable across versions per ADR-0061 §7.7.
 */
interface SlackBindingConfig {
  team_id?: string;
  team_name?: string | null;
  bot_user_id?: string;
  installer_user_id?: string;
  single_user_mode?: boolean;
  mode?: "Workspace" | "Org";
  bound_users?: Array<{ slack_user_id: string; tenant_user_id: string }>;
}

interface SlackBinding {
  teamId: string | null;
  teamName: string | null;
  botUserId: string | null;
  installerUserId: string | null;
  singleUserMode: boolean | null;
  boundUsers: Array<{ slackUserId: string; tenantUserId: string }>;
  boundAt: string | null;
}

function parseBinding(
  response: TenantConnectorBindingResponse | null | undefined,
): SlackBinding | null {
  if (!response) return null;
  // openapi-fetch types the `config` field as `JsonElement`. At runtime
  // we receive a plain JS object — `unknown` to TypeScript. Narrow defensively
  // so missing or malformed fields surface as nullable values instead of
  // crashing the render.
  const raw = (response.config ?? null) as SlackBindingConfig | null;
  if (raw === null || typeof raw !== "object") {
    return null;
  }
  return {
    teamId: typeof raw.team_id === "string" ? raw.team_id : null,
    teamName: typeof raw.team_name === "string" ? raw.team_name : null,
    botUserId: typeof raw.bot_user_id === "string" ? raw.bot_user_id : null,
    installerUserId:
      typeof raw.installer_user_id === "string" ? raw.installer_user_id : null,
    singleUserMode:
      typeof raw.single_user_mode === "boolean" ? raw.single_user_mode : null,
    boundUsers: Array.isArray(raw.bound_users)
      ? raw.bound_users
          .filter(
            (u): u is { slack_user_id: string; tenant_user_id: string } =>
              typeof u === "object" &&
              u !== null &&
              typeof (u as { slack_user_id?: unknown }).slack_user_id ===
                "string" &&
              typeof (u as { tenant_user_id?: unknown }).tenant_user_id ===
                "string",
          )
          .map((u) => ({
            slackUserId: u.slack_user_id,
            tenantUserId: u.tenant_user_id,
          }))
      : [],
    boundAt: response.boundAt ?? null,
  };
}

/**
 * `boundAt` carries the wire-form `DateTimeOffset` (e.g. `"2026-01-15T…"`).
 * The Slack connector currently writes `DateTimeOffset.UtcNow` only on the
 * PUT path; the GET path returns `DateTimeOffset.MinValue` (i.e. `"0001-01-01T…"`).
 * Detect that sentinel and render "—" instead of an impossible date.
 */
function formatBoundAt(value: string | null): string | null {
  if (!value) return null;
  const date = new Date(value);
  if (!Number.isFinite(date.getTime()) || date.getFullYear() < 2000) {
    return null;
  }
  return date.toLocaleString();
}

const SLACK_DOCS_URL =
  "https://github.com/cvoya-com/spring-voyage/blob/main/docs/decisions/0061-slack-connector-oss-shape.md";

/**
 * Decodes a Slack-related ProblemDetails into a presentable error tuple.
 * Falls back to the standard translator for anything we don't recognise.
 */
type SlackErrorKind = "enterprise-grid" | "not-configured" | "generic";

interface SlackError {
  kind: SlackErrorKind;
  title: string;
  detail: string;
}

function describeSlackError(err: unknown): SlackError {
  if (err instanceof ApiError && err.problem) {
    const code =
      typeof err.problem.code === "string" ? err.problem.code : undefined;
    if (code === "SlackEnterpriseGridUnsupported") {
      return {
        kind: "enterprise-grid",
        title: "Slack Enterprise Grid isn't supported in v0.1.",
        detail:
          typeof err.problem.detail === "string"
            ? err.problem.detail
            : "ADR-0061 §2.4 — workspace installs are the only path that lands a binding. The Grid org-level install is a forward-compat slot tracked for a later release.",
      };
    }
    if (err.status === 502) {
      return {
        kind: "not-configured",
        title: "Slack OAuth isn't configured on this deployment.",
        detail:
          typeof err.problem.detail === "string"
            ? err.problem.detail
            : "An operator needs to register a Slack app and set Slack:OAuth:ClientId / ClientSecret / SigningSecret / RedirectUri in spring.env before this surface can start an install.",
      };
    }
  }
  return {
    kind: "generic",
    title: "Couldn't start the Slack install.",
    detail: formatTranslatedError(err),
  };
}

/** Notice kinds the panel surfaces for client-side observations. */
type SlackOAuthNotice = "popup-closed" | "timed-out" | "aborted";

interface InstallState {
  status: "idle" | "starting" | "awaiting" | "failed";
  error: SlackError | null;
  /**
   * Latest cancellation/timeout outcome surfaced to the user. Distinct
   * from `error` because these don't carry a ProblemDetails — they're
   * client-side observations about the popup window.
   */
  notice: SlackOAuthNotice | null;
}

/**
 * Maps the postMessage error code from the backend's HTML handoff
 * into the panel's three-way error palette. The error code matches
 * the discriminator the backend uses (see
 * `SlackOAuthEndpoints.CallbackMessageType`); the messages it
 * carries are the same human-readable strings the previous JSON
 * ProblemDetails carried.
 */
function describeCallbackError(error: string, message: string): SlackError {
  if (error === "SlackEnterpriseGridUnsupported") {
    return {
      kind: "enterprise-grid",
      title: "Slack Enterprise Grid isn't supported in v0.1.",
      detail:
        message ||
        "ADR-0061 §2.4 — workspace installs are the only path that lands a binding. The Grid org-level install is a forward-compat slot tracked for a later release.",
    };
  }
  if (error === "oauth_not_configured") {
    return {
      kind: "not-configured",
      title: "Slack OAuth isn't configured on this deployment.",
      detail:
        message ||
        "An operator needs to register a Slack app and set Slack:OAuth:ClientId / ClientSecret / SigningSecret / RedirectUri in spring.env before this surface can start an install.",
    };
  }
  return {
    kind: "generic",
    title: "Couldn't complete the Slack install.",
    detail: message || `Slack returned error code '${error}'.`,
  };
}

const INITIAL_INSTALL_STATE: InstallState = {
  status: "idle",
  error: null,
  notice: null,
};

export function SlackConnectorPanel() {
  const { toast } = useToast();
  const queryClient = useQueryClient();

  const bindingQuery = useTenantSlackBinding();
  const binding = useMemo(() => parseBinding(bindingQuery.data), [bindingQuery.data]);

  const [installState, setInstallState] = useState<InstallState>(
    INITIAL_INSTALL_STATE,
  );
  const [confirmDisconnectOpen, setConfirmDisconnectOpen] = useState(false);
  const [disconnecting, setDisconnecting] = useState(false);

  // Track the live popup + the abort signal so the user can navigate
  // away mid-install without orphaning the polling loop.
  const popupRef = useRef<Window | null>(null);
  const abortRef = useRef<AbortController | null>(null);

  useEffect(() => {
    return () => {
      abortRef.current?.abort();
      // Don't auto-close the popup on parent unmount — the user might
      // still be completing OAuth. The popup will close itself once the
      // callback runs.
    };
  }, []);

  const refreshBinding = useCallback(async () => {
    await queryClient.invalidateQueries({
      queryKey: queryKeys.connectors.tenantBinding("slack"),
    });
  }, [queryClient]);

  const startInstall = useCallback(async () => {
    // Open the popup synchronously so the browser treats it as a
    // user-initiated window — many browsers block `window.open` calls
    // that happen after an `await`. Navigate it once the authorize URL
    // resolves.
    const popup = window.open(
      "about:blank",
      "spring-voyage-slack-oauth",
      "popup,width=720,height=820",
    );
    if (popup === null) {
      setInstallState({
        status: "failed",
        error: {
          kind: "generic",
          title: "Your browser blocked the Slack authorization window.",
          detail: "Allow popups for this site and try again.",
        },
        notice: null,
      });
      return;
    }
    popupRef.current = popup;
    setInstallState({ status: "starting", error: null, notice: null });

    let authorizeUrl: string;
    try {
      const result = await api.beginSlackOAuthAuthorize({
        clientState: buildSlackOAuthClientState(),
      });
      authorizeUrl = result.authorizeUrl;
    } catch (err) {
      popup.close();
      popupRef.current = null;
      setInstallState({
        status: "failed",
        error: describeSlackError(err),
        notice: null,
      });
      return;
    }

    popup.location.href = authorizeUrl;
    popup.focus();
    setInstallState({ status: "awaiting", error: null, notice: null });

    const abortController = new AbortController();
    abortRef.current?.abort();
    abortRef.current = abortController;

    // Issue #2837: the OAuth callback page posts a structured message
    // back to this window with the outcome. No more polling — the
    // handoff is synchronous from the popup, with the popup-closed /
    // timed-out / aborted observations as safety nets.
    const outcome: SlackOAuthHandoffOutcome = await awaitSlackOAuthHandoff({
      popup,
      signal: abortController.signal,
    });

    try {
      popup.close();
    } catch {
      // The popup is allowed to close itself first via the callback
      // HTML's `window.close()` call — the second close here is a
      // no-op when the window is already gone.
    }
    popupRef.current = null;
    abortRef.current = null;

    if (outcome.kind === "success") {
      setInstallState(INITIAL_INSTALL_STATE);
      await refreshBinding();
      toast({
        title: "Slack workspace connected",
        description: "The bot can now post DMs on behalf of this tenant.",
      });
      return;
    }

    if (outcome.kind === "error") {
      // Server-side error from the OAuth callback — Enterprise Grid,
      // workspace conflict, exchange failure, etc. Still refresh the
      // binding so any successfully persisted state replaces this
      // panel; the error banner is what the user actually reads.
      await refreshBinding();
      setInstallState({
        status: "failed",
        error: describeCallbackError(outcome.error, outcome.message),
        notice: null,
      });
      return;
    }

    // Client-side observation — popup closed, deadline elapsed, or
    // the listener was aborted. Re-fetch the binding so a successful
    // install that landed before the popup-closed signal fired still
    // flips the panel to bound state.
    await refreshBinding();
    setInstallState({ status: "failed", error: null, notice: outcome.kind });
  }, [refreshBinding, toast]);

  const disconnect = useCallback(async () => {
    setDisconnecting(true);
    try {
      await api.disconnectSlackBinding();
      await refreshBinding();
      toast({
        title: "Slack disconnected",
        description: "The bot token has been revoked and the binding removed.",
      });
      setConfirmDisconnectOpen(false);
    } catch (err) {
      toast({
        title: "Couldn't disconnect Slack",
        description: formatTranslatedError(err),
        variant: "destructive",
      });
    } finally {
      setDisconnecting(false);
    }
  }, [refreshBinding, toast]);

  // Loading state — first render before the binding query resolves.
  if (bindingQuery.isPending) {
    return (
      <div className="space-y-3" data-testid="slack-panel-loading">
        <Skeleton className="h-4 w-40" />
        <Skeleton className="h-9 w-48" />
      </div>
    );
  }

  if (bindingQuery.error) {
    return (
      <div
        role="alert"
        className="rounded-md border border-destructive/50 bg-destructive/10 px-3 py-2 text-sm text-destructive"
        data-testid="slack-panel-load-error"
      >
        {formatTranslatedError(bindingQuery.error)}
      </div>
    );
  }

  if (binding !== null) {
    return (
      <>
        <BoundState
          binding={binding}
          onReconnect={startInstall}
          onDisconnect={() => setConfirmDisconnectOpen(true)}
          installState={installState}
        />
        <ConfirmDialog
          open={confirmDisconnectOpen}
          title="Disconnect Slack workspace?"
          description={
            binding.teamName
              ? `This revokes the bot token for "${binding.teamName}" and removes the binding from this tenant. Agents will stop posting to Slack until you reconnect.`
              : "This revokes the bot token and removes the binding from this tenant. Agents will stop posting to Slack until you reconnect."
          }
          confirmLabel="Disconnect"
          cancelLabel="Cancel"
          confirmVariant="destructive"
          onConfirm={disconnect}
          onCancel={() => setConfirmDisconnectOpen(false)}
          pending={disconnecting}
        />
      </>
    );
  }

  return (
    <EmptyState
      installState={installState}
      onInstall={startInstall}
      onDismissNotice={() =>
        setInstallState((prev) => ({ ...prev, notice: null }))
      }
    />
  );
}

function EmptyState({
  installState,
  onInstall,
  onDismissNotice,
}: {
  installState: InstallState;
  onInstall: () => void;
  onDismissNotice: () => void;
}) {
  const installing =
    installState.status === "starting" || installState.status === "awaiting";

  return (
    <div className="space-y-4" data-testid="slack-panel-empty">
      <p className="text-sm text-muted-foreground">
        Connect a Slack workspace so this tenant&apos;s agents can post DMs to
        the installer. v0.1 supports a single bound user (the OAuth installer)
        and direct messages only —{" "}
        <a
          href={SLACK_DOCS_URL}
          target="_blank"
          rel="noopener noreferrer"
          className="inline-flex items-center gap-1 text-primary hover:underline"
        >
          read the limitations
          <ExternalLink className="h-3 w-3" aria-hidden="true" />
        </a>
        .
      </p>

      <div className="flex flex-wrap items-center gap-3">
        <Button
          variant="default"
          onClick={onInstall}
          disabled={installing}
          aria-busy={installing}
          data-testid="slack-panel-install"
        >
          {installing ? (
            <Loader2
              className="mr-1 h-4 w-4 animate-spin"
              aria-hidden="true"
            />
          ) : (
            <Slack className="mr-1 h-4 w-4" aria-hidden="true" />
          )}
          {installState.status === "starting"
            ? "Opening Slack…"
            : installState.status === "awaiting"
              ? "Waiting for Slack…"
              : "Install in Slack workspace"}
        </Button>
        {installState.status === "awaiting" && (
          <span className="text-xs text-muted-foreground">
            Complete the install in the Slack window. This panel will refresh
            when it&apos;s done.
          </span>
        )}
      </div>

      {installState.error !== null && (
        <ErrorBanner
          error={installState.error}
          onRetry={onInstall}
          retrying={installing}
        />
      )}

      {installState.error === null && installState.notice !== null && (
        <NoticeBanner notice={installState.notice} onDismiss={onDismissNotice} />
      )}
    </div>
  );
}

function BoundState({
  binding,
  onReconnect,
  onDisconnect,
  installState,
}: {
  binding: SlackBinding;
  onReconnect: () => void;
  onDisconnect: () => void;
  installState: InstallState;
}) {
  const boundAtLabel = formatBoundAt(binding.boundAt);
  const installer = binding.boundUsers[0] ?? null;
  const reconnecting =
    installState.status === "starting" || installState.status === "awaiting";

  return (
    <div className="space-y-4" data-testid="slack-panel-bound">
      <div className="flex flex-wrap items-center gap-2">
        <Badge variant="outline" className="gap-1">
          <CheckCircle2
            className="h-3 w-3 text-success"
            aria-hidden="true"
          />
          Connected
        </Badge>
        {binding.singleUserMode && (
          <Badge variant="secondary" className="gap-1">
            <User className="h-3 w-3" aria-hidden="true" />
            Single-user mode
          </Badge>
        )}
      </div>

      <dl className="grid grid-cols-1 gap-x-6 gap-y-2 sm:grid-cols-[max-content_1fr]">
        <BoundFact
          icon={<Hash className="h-4 w-4" aria-hidden="true" />}
          label="Workspace"
          value={
            binding.teamName ??
            binding.teamId ??
            "(unknown — see CLI for details)"
          }
          testId="slack-panel-bound-workspace"
          mono={binding.teamName === null}
        />
        {binding.teamId && (
          <BoundFact
            label="Team ID"
            value={binding.teamId}
            testId="slack-panel-bound-team-id"
            mono
          />
        )}
        <BoundFact
          icon={<MessageSquare className="h-4 w-4" aria-hidden="true" />}
          label="Bot user"
          value={binding.botUserId ?? "(unknown)"}
          testId="slack-panel-bound-bot-user"
          mono={binding.botUserId !== null}
        />
        <BoundFact
          icon={<User className="h-4 w-4" aria-hidden="true" />}
          label="Installer (Slack)"
          value={binding.installerUserId ?? "(unknown)"}
          testId="slack-panel-bound-installer"
          mono={binding.installerUserId !== null}
        />
        {installer && (
          <BoundFact
            label="Bound TenantUser"
            value={installer.tenantUserId}
            testId="slack-panel-bound-tenant-user"
            mono
          />
        )}
        {boundAtLabel !== null && (
          <BoundFact
            label="Connected since"
            value={boundAtLabel}
            testId="slack-panel-bound-since"
          />
        )}
      </dl>

      <p className="text-xs text-muted-foreground">
        v0.1 routes messages as DMs to the installer only —{" "}
        <a
          href={SLACK_DOCS_URL}
          target="_blank"
          rel="noopener noreferrer"
          className="inline-flex items-center gap-1 text-primary hover:underline"
        >
          ADR-0061 §2
          <ExternalLink className="h-3 w-3" aria-hidden="true" />
        </a>
        .
      </p>

      <div className="flex flex-wrap items-center gap-2">
        <Button
          variant="outline"
          size="sm"
          onClick={onReconnect}
          disabled={reconnecting}
          aria-busy={reconnecting}
          data-testid="slack-panel-reconnect"
        >
          {reconnecting ? (
            <Loader2
              className="mr-1 h-4 w-4 animate-spin"
              aria-hidden="true"
            />
          ) : (
            <RefreshCw className="mr-1 h-4 w-4" aria-hidden="true" />
          )}
          {reconnecting ? "Reconnecting…" : "Reconnect"}
        </Button>
        <Button
          variant="destructive"
          size="sm"
          onClick={onDisconnect}
          data-testid="slack-panel-disconnect"
        >
          <Unplug className="mr-1 h-4 w-4" aria-hidden="true" />
          Disconnect
        </Button>
      </div>

      {installState.error !== null && (
        <ErrorBanner
          error={installState.error}
          onRetry={onReconnect}
          retrying={reconnecting}
        />
      )}
    </div>
  );
}

function BoundFact({
  icon,
  label,
  value,
  testId,
  mono,
}: {
  icon?: React.ReactNode;
  label: string;
  value: string;
  testId?: string;
  mono?: boolean;
}) {
  return (
    <>
      <dt className="flex items-center gap-1.5 text-xs uppercase tracking-wide text-muted-foreground">
        {icon}
        {label}
      </dt>
      <dd
        className={
          mono
            ? "break-all font-mono text-xs text-foreground"
            : "text-sm text-foreground"
        }
        data-testid={testId}
      >
        {value}
      </dd>
    </>
  );
}

function ErrorBanner({
  error,
  onRetry,
  retrying,
}: {
  error: SlackError;
  onRetry: () => void;
  retrying: boolean;
}) {
  const palette =
    error.kind === "enterprise-grid"
      ? "border-warning/50 bg-warning/10 text-warning"
      : error.kind === "not-configured"
        ? "border-info/50 bg-info/15 text-info"
        : "border-destructive/50 bg-destructive/10 text-destructive";

  // "not-configured" + "enterprise-grid" don't benefit from a retry —
  // the operator has to change something off-portal first. Only render
  // the retry button for generic errors.
  const showRetry = error.kind === "generic";

  return (
    <div
      role="alert"
      className={`space-y-2 rounded-md border px-3 py-2 text-sm ${palette}`}
      data-testid={`slack-panel-error-${error.kind}`}
    >
      <p className="flex items-start gap-2 font-medium">
        <AlertTriangle
          className="mt-0.5 h-4 w-4 flex-none"
          aria-hidden="true"
        />
        {error.title}
      </p>
      <p className="text-xs text-foreground">{error.detail}</p>
      {error.kind === "enterprise-grid" && (
        <p className="text-xs text-foreground">
          See{" "}
          <a
            href={SLACK_DOCS_URL}
            target="_blank"
            rel="noopener noreferrer"
            className="inline-flex items-center gap-1 underline"
          >
            ADR-0061 §2.4
            <ExternalLink className="h-3 w-3" aria-hidden="true" />
          </a>{" "}
          for the rationale.
        </p>
      )}
      {showRetry && (
        <Button
          size="sm"
          variant="outline"
          onClick={onRetry}
          disabled={retrying}
          aria-busy={retrying}
          data-testid="slack-panel-error-retry"
        >
          {retrying && (
            <Loader2 className="mr-1 h-4 w-4 animate-spin" aria-hidden="true" />
          )}
          Try again
        </Button>
      )}
    </div>
  );
}

function NoticeBanner({
  notice,
  onDismiss,
}: {
  notice: SlackOAuthNotice;
  onDismiss: () => void;
}) {
  const message = noticeMessageFor(notice);
  return (
    <div
      role="status"
      className="flex items-start justify-between gap-3 rounded-md border border-border bg-muted/40 px-3 py-2 text-sm text-muted-foreground"
      data-testid={`slack-panel-notice-${notice}`}
    >
      <span>{message}</span>
      <button
        type="button"
        onClick={onDismiss}
        className="text-xs text-primary hover:underline"
        aria-label="Dismiss notice"
      >
        Dismiss
      </button>
    </div>
  );
}

function noticeMessageFor(notice: SlackOAuthNotice): string {
  switch (notice) {
    case "popup-closed":
      return "Slack install cancelled. Click Install in Slack workspace to try again.";
    case "timed-out":
      return `Slack install didn't complete within ${Math.round(SLACK_OAUTH_HANDOFF_TIMEOUT_MS / 60000)} minutes. Click Install in Slack workspace to retry.`;
    case "aborted":
      return "Slack install was interrupted. Click Install in Slack workspace to retry.";
  }
}
