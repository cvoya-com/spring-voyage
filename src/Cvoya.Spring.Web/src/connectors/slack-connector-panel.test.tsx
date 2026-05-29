import { act, fireEvent, render, screen, waitFor } from "@testing-library/react";
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { beforeEach, describe, expect, it, vi } from "vitest";
import type { ReactElement, ReactNode } from "react";

// Mock the shared API client before importing the panel so the module
// graph sees the stub. The bound-state and Enterprise-Grid branches
// both check `err instanceof ApiError`, so the mock exposes the real
// class shape — a bare object would fail the instanceof check.
vi.mock("@/lib/api/client", async () => {
  class MockApiError extends Error {
    public readonly problem: unknown;
    constructor(
      public readonly status: number,
      public readonly statusText: string,
      public readonly body: unknown,
    ) {
      super(`API error ${status}: ${statusText}`);
      this.name = "ApiError";
      this.problem =
        body && typeof body === "object"
          ? { ...(body as Record<string, unknown>) }
          : undefined;
    }
  }
  return {
    ApiError: MockApiError,
    api: {
      getTenantSlackBinding: vi.fn(),
      beginSlackOAuthAuthorize: vi.fn(),
      disconnectSlackBinding: vi.fn(),
    },
  };
});

// Mock the OAuth-handoff helper so tests don't actually wait for a
// real postMessage dispatch. Each test wires its own outcome.
vi.mock("@connector-slack/slack-oauth-browser", () => ({
  awaitSlackOAuthHandoff: vi.fn(),
  buildSlackOAuthClientState: vi.fn(() => null),
  SLACK_OAUTH_CALLBACK_MESSAGE_TYPE: "sv:slack:oauth:done",
  SLACK_OAUTH_HANDOFF_TIMEOUT_MS: 1,
}));

// The panel calls `useRouter().push` to navigate the empty-state CTA to
// the one-page install wizard (#2882). Mock `next/navigation` so the
// hook resolves outside an App Router context and the push is assertable.
const mockPush = vi.fn();
vi.mock("next/navigation", () => ({
  useRouter: () => ({
    push: mockPush,
    replace: vi.fn(),
    prefetch: vi.fn(),
    back: vi.fn(),
    forward: vi.fn(),
    refresh: vi.fn(),
  }),
}));

import { ApiError, api } from "@/lib/api/client";
import type { TenantConnectorBindingResponse } from "@/lib/api/types";
import { SlackConnectorPanel } from "@connector-slack/connector-panel";
import { awaitSlackOAuthHandoff } from "@connector-slack/slack-oauth-browser";

const mockedApi = vi.mocked(api);
const mockedHandoff = vi.mocked(awaitSlackOAuthHandoff);

function makeBinding(
  config: Record<string, unknown>,
): TenantConnectorBindingResponse {
  return {
    connectorSlug: "slack",
    typeId: "2c8d5b1f-9a4e-4f8b-b7c3-3e1d4a5b6c70",
    boundAt: "2026-05-26T10:00:00Z",
    config: config as TenantConnectorBindingResponse["config"],
  };
}

const BOUND_CONFIG = {
  team_id: "T12345",
  team_name: "Acme Engineering",
  bot_user_id: "U_BOT",
  installer_user_id: "U_OP",
  bot_token_secret_name: "x",
  signing_secret_secret_name: "y",
  single_user_mode: true,
  mode: "Workspace",
  bound_users: [],
};

function wrap(): {
  client: QueryClient;
  wrapper: (props: { children: ReactNode }) => ReactElement;
} {
  const client = new QueryClient({
    defaultOptions: { queries: { retry: false, gcTime: 0 } },
  });
  const wrapper = ({ children }: { children: ReactNode }) => (
    <QueryClientProvider client={client}>{children}</QueryClientProvider>
  );
  wrapper.displayName = "QueryWrapper";
  return { client, wrapper };
}

/**
 * Renders the panel in its bound state and clicks Reconnect. Reconnect
 * is the inline OAuth-popup path that survives the #2882 wizard rewrite
 * (the empty-state CTA now routes to the wizard instead). A popup stub is
 * installed so `window.open` returns a Window-shaped object.
 */
async function renderBoundAndReconnect(): Promise<{ popupStub: { close: ReturnType<typeof vi.fn> } }> {
  const popupStub = { close: vi.fn(), focus: vi.fn(), location: { href: "" } };
  vi.spyOn(window, "open").mockReturnValue(popupStub as unknown as Window);

  const { wrapper } = wrap();
  await act(async () => {
    render(<SlackConnectorPanel />, { wrapper });
  });
  await screen.findByTestId("slack-panel-bound");
  await act(async () => {
    fireEvent.click(screen.getByTestId("slack-panel-reconnect"));
  });
  return { popupStub };
}

describe("SlackConnectorPanel", () => {
  beforeEach(() => {
    vi.clearAllMocks();
  });

  // The empty state is the first thing the operator sees on a fresh
  // tenant. The CTA exists, the docs link points at the limitations
  // ADR, and the panel surfaces no loading errors.
  it("renders the install CTA when no binding exists", async () => {
    mockedApi.getTenantSlackBinding.mockResolvedValue(null);

    const { wrapper } = wrap();
    await act(async () => {
      render(<SlackConnectorPanel />, { wrapper });
    });

    const empty = await screen.findByTestId("slack-panel-empty");
    expect(empty).toBeInTheDocument();

    const cta = screen.getByTestId("slack-panel-install");
    expect(cta).toBeInTheDocument();
    expect(cta).toHaveTextContent(/Install in Slack workspace/);
    expect(cta).not.toBeDisabled();

    // The "Connect Slack" copy includes a docs link pointing at the
    // ADR — this is the contract the brief calls out for an
    // "actionable limitation" message.
    expect(empty).toHaveTextContent(/read the limitations/);
  });

  // #2882: the empty-state CTA routes to the one-page install wizard
  // (which registers the Slack app via the Manifest API) rather than
  // starting OAuth inline.
  it("routes to the install wizard when the empty-state CTA is clicked", async () => {
    mockedApi.getTenantSlackBinding.mockResolvedValue(null);

    const { wrapper } = wrap();
    await act(async () => {
      render(<SlackConnectorPanel />, { wrapper });
    });

    await act(async () => {
      fireEvent.click(await screen.findByTestId("slack-panel-install"));
    });

    expect(mockPush).toHaveBeenCalledWith("/connectors/slack/install");
    // The empty-state CTA must NOT start OAuth inline anymore.
    expect(mockedApi.beginSlackOAuthAuthorize).not.toHaveBeenCalled();
  });

  // The bound state surfaces every field the brief calls out:
  // workspace name, bot user, installer Slack user id, bound
  // TenantUser id, connected-since.
  it("renders the bound state with workspace, bot, installer, and tenant-user identity", async () => {
    mockedApi.getTenantSlackBinding.mockResolvedValue(
      makeBinding({
        team_id: "T12345",
        team_name: "Acme Engineering",
        bot_user_id: "U_BOT_123",
        installer_user_id: "U_OP_456",
        bot_token_secret_name: "slack/bot-token",
        signing_secret_secret_name: "slack/signing-secret",
        single_user_mode: true,
        mode: "Workspace",
        bound_users: [
          {
            slack_user_id: "U_OP_456",
            tenant_user_id: "11111111-2222-3333-4444-555555555555",
          },
        ],
      }),
    );

    const { wrapper } = wrap();
    await act(async () => {
      render(<SlackConnectorPanel />, { wrapper });
    });

    const bound = await screen.findByTestId("slack-panel-bound");
    expect(bound).toBeInTheDocument();

    expect(screen.getByTestId("slack-panel-bound-workspace")).toHaveTextContent(
      "Acme Engineering",
    );
    expect(screen.getByTestId("slack-panel-bound-team-id")).toHaveTextContent(
      "T12345",
    );
    expect(screen.getByTestId("slack-panel-bound-bot-user")).toHaveTextContent(
      "U_BOT_123",
    );
    expect(screen.getByTestId("slack-panel-bound-installer")).toHaveTextContent(
      "U_OP_456",
    );
    expect(
      screen.getByTestId("slack-panel-bound-tenant-user"),
    ).toHaveTextContent("11111111-2222-3333-4444-555555555555");
    // Connected-since renders from the `boundAt` field via toLocaleString.
    expect(
      screen.getByTestId("slack-panel-bound-since"),
    ).toBeInTheDocument();
  });

  // The disconnect confirmation modal must NOT auto-focus the
  // destructive primary action — Cancel comes first in DOM order so
  // the dialog's focus-first-focusable behaviour lands on Cancel.
  it("opens the disconnect confirmation modal with Cancel focused, not Disconnect", async () => {
    mockedApi.getTenantSlackBinding.mockResolvedValue(makeBinding(BOUND_CONFIG));

    const { wrapper } = wrap();
    await act(async () => {
      render(<SlackConnectorPanel />, { wrapper });
    });

    await act(async () => {
      fireEvent.click(await screen.findByTestId("slack-panel-disconnect"));
    });

    // The dialog mounts; both Cancel and the confirm-Disconnect are
    // rendered. The Dialog primitive focuses the first focusable
    // element inside the modal panel on open. The ConfirmDialog
    // renders Cancel BEFORE the destructive confirm action — assert
    // that's still true so the a11y rule "do not auto-focus a
    // destructive button" stays held. The page also contains the
    // panel-side "Disconnect" button, but it lives outside the
    // dialog's focus scope; scope the query to the dialog by role.
    const dialog = await screen.findByRole("dialog");
    const cancel = await screen.findByRole("button", { name: "Cancel" });
    const buttonsInDialog = Array.from(
      dialog.querySelectorAll<HTMLButtonElement>("button"),
    );
    const cancelIndex = buttonsInDialog.findIndex((b) => b === cancel);
    const confirmIndex = buttonsInDialog.findIndex(
      (b) => b.textContent === "Disconnect",
    );
    expect(cancelIndex).toBeGreaterThan(-1);
    expect(confirmIndex).toBeGreaterThan(cancelIndex);
    expect(document.activeElement).toBe(cancel);
  });

  it("invokes disconnectSlackBinding when the confirmation is accepted", async () => {
    mockedApi.getTenantSlackBinding.mockResolvedValueOnce(
      makeBinding(BOUND_CONFIG),
    );
    // After the disconnect call resolves, the panel refreshes the
    // binding query and should see "no binding" — flips to empty state.
    mockedApi.getTenantSlackBinding.mockResolvedValue(null);
    mockedApi.disconnectSlackBinding.mockResolvedValue(undefined);

    const { wrapper } = wrap();
    await act(async () => {
      render(<SlackConnectorPanel />, { wrapper });
    });

    await act(async () => {
      fireEvent.click(await screen.findByTestId("slack-panel-disconnect"));
    });
    // The dialog's confirm button is the second "Disconnect"-named
    // button on the page — scope the query to the dialog.
    const dialog = await screen.findByRole("dialog");
    const confirmBtn = Array.from(
      dialog.querySelectorAll<HTMLButtonElement>("button"),
    ).find((b) => b.textContent === "Disconnect");
    expect(confirmBtn).toBeDefined();
    await act(async () => {
      fireEvent.click(confirmBtn!);
    });

    await waitFor(() =>
      expect(mockedApi.disconnectSlackBinding).toHaveBeenCalledOnce(),
    );
    // Panel returns to the empty state once the cache invalidation
    // re-reads the binding and gets `null`.
    await screen.findByTestId("slack-panel-empty");
  });

  // ---- Reconnect (inline OAuth popup) — the path that survives the
  // wizard rewrite. A bound tenant re-runs OAuth without re-creating the
  // Slack app, so the credentials already exist; startInstall drives the
  // popup + postMessage handoff exactly as before. ----

  it("reconnect: re-runs OAuth and stays bound when the handoff reports success", async () => {
    mockedApi.getTenantSlackBinding.mockResolvedValue(makeBinding(BOUND_CONFIG));
    mockedApi.beginSlackOAuthAuthorize.mockResolvedValue({
      authorizeUrl: "https://slack.com/oauth/v2/authorize?state=abc",
      state: "abc",
    });
    mockedHandoff.mockResolvedValue({ kind: "success" });

    await renderBoundAndReconnect();

    expect(mockedApi.beginSlackOAuthAuthorize).toHaveBeenCalledOnce();
    expect(mockedHandoff).toHaveBeenCalledOnce();
    // Still bound, no error banner.
    expect(screen.getByTestId("slack-panel-bound")).toBeInTheDocument();
    expect(
      screen.queryByTestId("slack-panel-error-generic"),
    ).not.toBeInTheDocument();
  });

  // ADR-0061 §2.4 — Grid is refused at install time. When the authorize
  // start returns the Grid code, surface the labelled Grid banner with
  // no retry CTA (the operator can't recover from this on-portal).
  it("reconnect: renders the Enterprise-Grid banner when the OAuth start fails with that code", async () => {
    mockedApi.getTenantSlackBinding.mockResolvedValue(makeBinding(BOUND_CONFIG));
    mockedApi.beginSlackOAuthAuthorize.mockRejectedValue(
      new ApiError(422, "Unprocessable Entity", {
        title: "Slack Enterprise Grid is not supported",
        detail: "ADR-0061 §2.4 — Grid is refused at install time in v0.1.",
        code: "SlackEnterpriseGridUnsupported",
        enterprise_id: "E123",
      }),
    );

    const { popupStub } = await renderBoundAndReconnect();

    const error = await screen.findByTestId("slack-panel-error-enterprise-grid");
    expect(error).toHaveTextContent(/Slack Enterprise Grid isn't supported in v0\.1/);
    expect(
      screen.queryByTestId("slack-panel-error-retry"),
    ).not.toBeInTheDocument();
    expect(popupStub.close).toHaveBeenCalled();
  });

  // The 502 path — Slack OAuth options are not configured across any
  // persistence tier. Surfaced with the "not configured" palette and no
  // retry button (operator change required first).
  it("reconnect: renders the not-configured error when the OAuth start returns 502", async () => {
    mockedApi.getTenantSlackBinding.mockResolvedValue(makeBinding(BOUND_CONFIG));
    mockedApi.beginSlackOAuthAuthorize.mockRejectedValue(
      new ApiError(502, "Bad Gateway", {
        title: "Slack OAuth is not configured",
        detail:
          "An operator needs to run `spring connector slack install` with one of --write-env, --write-secrets, or --write-tenant-secrets.",
      }),
    );

    await renderBoundAndReconnect();

    expect(
      await screen.findByTestId("slack-panel-error-not-configured"),
    ).toHaveTextContent(/Slack OAuth isn't configured on this deployment/);
  });

  // Generic errors surface the "Try again" CTA so a transient network
  // failure isn't a dead-end.
  it("reconnect: renders the generic-error retry CTA on an unknown OAuth start failure", async () => {
    mockedApi.getTenantSlackBinding.mockResolvedValue(makeBinding(BOUND_CONFIG));
    mockedApi.beginSlackOAuthAuthorize.mockRejectedValue(new Error("boom"));

    await renderBoundAndReconnect();

    expect(
      await screen.findByTestId("slack-panel-error-generic"),
    ).toBeInTheDocument();
    expect(screen.getByTestId("slack-panel-error-retry")).toBeInTheDocument();
  });

  // Issue #2837: server-side Grid refusal arrives via postMessage, NOT
  // via the authorize endpoint. The error code on the message is
  // `SlackEnterpriseGridUnsupported`; the panel renders the Grid banner.
  it("reconnect: renders Grid-refusal banner when the OAuth callback posts a Grid error", async () => {
    mockedApi.getTenantSlackBinding.mockResolvedValue(makeBinding(BOUND_CONFIG));
    mockedApi.beginSlackOAuthAuthorize.mockResolvedValue({
      authorizeUrl: "https://slack.com/oauth/v2/authorize?state=abc",
      state: "abc",
    });
    mockedHandoff.mockResolvedValue({
      kind: "error",
      error: "SlackEnterpriseGridUnsupported",
      message: "Grid is not supported in v0.1.",
    });

    await renderBoundAndReconnect();

    expect(
      await screen.findByTestId("slack-panel-error-enterprise-grid"),
    ).toHaveTextContent(/Enterprise Grid/);
    expect(
      screen.queryByTestId("slack-panel-error-retry"),
    ).not.toBeInTheDocument();
  });

  // Other server-side error codes from the callback (workspace conflict,
  // exchange failure, etc.) surface as the generic banner with the
  // Try-again CTA. We pick the `exchange_failed` code from the union.
  it("reconnect: renders generic-error banner when the OAuth callback posts a non-Grid error", async () => {
    mockedApi.getTenantSlackBinding.mockResolvedValue(makeBinding(BOUND_CONFIG));
    mockedApi.beginSlackOAuthAuthorize.mockResolvedValue({
      authorizeUrl: "https://slack.com/oauth/v2/authorize?state=abc",
      state: "abc",
    });
    mockedHandoff.mockResolvedValue({
      kind: "error",
      error: "exchange_failed",
      message: "Slack returned invalid_code on oauth.v2.access.",
    });

    await renderBoundAndReconnect();

    expect(
      await screen.findByTestId("slack-panel-error-generic"),
    ).toHaveTextContent(/invalid_code/);
    expect(screen.getByTestId("slack-panel-error-retry")).toBeInTheDocument();
  });

  // Loading state on first render.
  it("renders a loading skeleton while the binding query is pending", async () => {
    // Never-resolving promise so the query stays in `isPending` state.
    mockedApi.getTenantSlackBinding.mockReturnValue(
      new Promise(() => {
        /* never */
      }),
    );

    const { wrapper } = wrap();
    render(<SlackConnectorPanel />, { wrapper });

    expect(screen.getByTestId("slack-panel-loading")).toBeInTheDocument();
  });

  // Hard error reading the binding (e.g., backend down) — render an
  // alert instead of pretending the panel is empty.
  it("renders a load-error banner when the binding query fails", async () => {
    mockedApi.getTenantSlackBinding.mockRejectedValue(
      new Error("network down"),
    );

    const { wrapper } = wrap();
    await act(async () => {
      render(<SlackConnectorPanel />, { wrapper });
    });

    expect(
      await screen.findByTestId("slack-panel-load-error"),
    ).toBeInTheDocument();
  });
});
