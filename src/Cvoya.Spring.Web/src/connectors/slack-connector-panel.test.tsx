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

  // ADR-0061 §2.4 — Grid is refused at install time with a structured
  // ProblemDetails (HTTP authorize start path). Surface it with a
  // labelled error instead of an "unknown error" fallback.
  it("renders the Enterprise-Grid error when the OAuth start fails with that code", async () => {
    mockedApi.getTenantSlackBinding.mockResolvedValue(null);
    mockedApi.beginSlackOAuthAuthorize.mockRejectedValue(
      new ApiError(422, "Unprocessable Entity", {
        title: "Slack Enterprise Grid is not supported",
        detail:
          "ADR-0061 §2.4 — Grid is refused at install time in v0.1.",
        code: "SlackEnterpriseGridUnsupported",
        enterprise_id: "E123",
      }),
    );

    // The popup is opened before the await; jsdom's window.open returns
    // a Window-shaped stub by default, so the panel reaches the
    // POST /oauth/authorize call.
    const popupStub = { close: vi.fn(), focus: vi.fn(), location: { href: "" } };
    const openSpy = vi
      .spyOn(window, "open")
      .mockReturnValue(popupStub as unknown as Window);

    const { wrapper } = wrap();
    await act(async () => {
      render(<SlackConnectorPanel />, { wrapper });
    });

    await screen.findByTestId("slack-panel-install");

    await act(async () => {
      fireEvent.click(screen.getByTestId("slack-panel-install"));
    });

    const error = await screen.findByTestId(
      "slack-panel-error-enterprise-grid",
    );
    expect(error).toHaveTextContent(
      /Slack Enterprise Grid isn't supported in v0\.1/,
    );
    // Generic-error retry CTA is suppressed for Grid — the operator
    // can't recover from this without going off-portal.
    expect(
      screen.queryByTestId("slack-panel-error-retry"),
    ).not.toBeInTheDocument();
    expect(popupStub.close).toHaveBeenCalled();

    openSpy.mockRestore();
  });

  // The 502 path — Slack OAuth options are not configured across any
  // persistence tier (tenant-secret / platform-secret / env-config).
  // Surfaced with the "not configured" palette and no retry button
  // (same reason as Grid — operator change required first).
  it("renders the not-configured error when the OAuth start returns 502", async () => {
    mockedApi.getTenantSlackBinding.mockResolvedValue(null);
    mockedApi.beginSlackOAuthAuthorize.mockRejectedValue(
      new ApiError(502, "Bad Gateway", {
        title: "Slack OAuth is not configured",
        detail:
          "An operator needs to run `spring connector slack install` with one of --write-env, --write-secrets, or --write-tenant-secrets.",
      }),
    );

    const popupStub = { close: vi.fn(), focus: vi.fn(), location: { href: "" } };
    const openSpy = vi
      .spyOn(window, "open")
      .mockReturnValue(popupStub as unknown as Window);

    const { wrapper } = wrap();
    await act(async () => {
      render(<SlackConnectorPanel />, { wrapper });
    });

    await act(async () => {
      fireEvent.click(
        await screen.findByTestId("slack-panel-install"),
      );
    });

    expect(
      await screen.findByTestId("slack-panel-error-not-configured"),
    ).toHaveTextContent(/Slack OAuth isn't configured on this deployment/);

    openSpy.mockRestore();
  });

  // Generic errors surface the "Try again" CTA so a transient network
  // failure isn't a dead-end.
  it("renders the generic-error retry CTA on an unknown OAuth start failure", async () => {
    mockedApi.getTenantSlackBinding.mockResolvedValue(null);
    mockedApi.beginSlackOAuthAuthorize.mockRejectedValue(new Error("boom"));

    const popupStub = { close: vi.fn(), focus: vi.fn(), location: { href: "" } };
    const openSpy = vi
      .spyOn(window, "open")
      .mockReturnValue(popupStub as unknown as Window);

    const { wrapper } = wrap();
    await act(async () => {
      render(<SlackConnectorPanel />, { wrapper });
    });

    await act(async () => {
      fireEvent.click(
        await screen.findByTestId("slack-panel-install"),
      );
    });

    expect(
      await screen.findByTestId("slack-panel-error-generic"),
    ).toBeInTheDocument();
    expect(
      screen.getByTestId("slack-panel-error-retry"),
    ).toBeInTheDocument();

    openSpy.mockRestore();
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
    mockedApi.getTenantSlackBinding.mockResolvedValue(
      makeBinding({
        team_id: "T12345",
        team_name: "Acme Engineering",
        bot_user_id: "U_BOT",
        installer_user_id: "U_OP",
        bot_token_secret_name: "x",
        signing_secret_secret_name: "y",
        single_user_mode: true,
        mode: "Workspace",
        bound_users: [],
      }),
    );

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
    const cancelIndex = buttonsInDialog.findIndex(
      (b) => b === cancel,
    );
    const confirmIndex = buttonsInDialog.findIndex(
      (b) => b.textContent === "Disconnect",
    );
    expect(cancelIndex).toBeGreaterThan(-1);
    expect(confirmIndex).toBeGreaterThan(cancelIndex);
    expect(document.activeElement).toBe(cancel);
  });

  it("invokes disconnectSlackBinding when the confirmation is accepted", async () => {
    mockedApi.getTenantSlackBinding.mockResolvedValueOnce(
      makeBinding({
        team_id: "T12345",
        team_name: "Acme Engineering",
        bot_user_id: "U_BOT",
        installer_user_id: "U_OP",
        bot_token_secret_name: "x",
        signing_secret_secret_name: "y",
        single_user_mode: true,
        mode: "Workspace",
        bound_users: [],
      }),
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

  // The success path of the install flow — the handoff reports
  // `success` via postMessage; the cache invalidation flips us to
  // bound state.
  it("flips to bound state when the postMessage handoff reports success", async () => {
    // First call (binding-query mount): empty.
    // Subsequent calls (after the handoff resolves): bound.
    let callCount = 0;
    mockedApi.getTenantSlackBinding.mockImplementation(() => {
      callCount++;
      if (callCount === 1) return Promise.resolve(null);
      return Promise.resolve(
        makeBinding({
          team_id: "T12345",
          team_name: "Acme Engineering",
          bot_user_id: "U_BOT",
          installer_user_id: "U_OP",
          bot_token_secret_name: "x",
          signing_secret_secret_name: "y",
          single_user_mode: true,
          mode: "Workspace",
          bound_users: [],
        }),
      );
    });
    mockedApi.beginSlackOAuthAuthorize.mockResolvedValue({
      authorizeUrl: "https://slack.com/oauth/v2/authorize?state=abc",
      state: "abc",
    });
    mockedHandoff.mockResolvedValue({ kind: "success" });

    const popupStub = { close: vi.fn(), focus: vi.fn(), location: { href: "" } };
    const openSpy = vi
      .spyOn(window, "open")
      .mockReturnValue(popupStub as unknown as Window);

    const { wrapper } = wrap();
    await act(async () => {
      render(<SlackConnectorPanel />, { wrapper });
    });

    await act(async () => {
      fireEvent.click(await screen.findByTestId("slack-panel-install"));
    });

    expect(mockedHandoff).toHaveBeenCalledOnce();
    await screen.findByTestId("slack-panel-bound");

    openSpy.mockRestore();
  });

  // Issue #2837: server-side Grid refusal arrives via postMessage,
  // NOT via the authorize endpoint. The error code on the message is
  // `SlackEnterpriseGridUnsupported`; the panel must render the same
  // Grid banner the authorize-error path uses.
  it("renders Grid-refusal banner when the OAuth callback posts a Grid error", async () => {
    mockedApi.getTenantSlackBinding.mockResolvedValue(null);
    mockedApi.beginSlackOAuthAuthorize.mockResolvedValue({
      authorizeUrl: "https://slack.com/oauth/v2/authorize?state=abc",
      state: "abc",
    });
    mockedHandoff.mockResolvedValue({
      kind: "error",
      error: "SlackEnterpriseGridUnsupported",
      message: "Grid is not supported in v0.1.",
    });

    const popupStub = { close: vi.fn(), focus: vi.fn(), location: { href: "" } };
    const openSpy = vi
      .spyOn(window, "open")
      .mockReturnValue(popupStub as unknown as Window);

    const { wrapper } = wrap();
    await act(async () => {
      render(<SlackConnectorPanel />, { wrapper });
    });

    await act(async () => {
      fireEvent.click(await screen.findByTestId("slack-panel-install"));
    });

    expect(
      await screen.findByTestId("slack-panel-error-enterprise-grid"),
    ).toHaveTextContent(/Enterprise Grid/);
    // Grid → no retry CTA (operator must change something off-portal
    // first; same rule as the authorize-error path).
    expect(
      screen.queryByTestId("slack-panel-error-retry"),
    ).not.toBeInTheDocument();

    openSpy.mockRestore();
  });

  // Other server-side error codes from the callback (workspace
  // conflict, exchange failure, etc.) surface as the generic banner
  // with the Try-again CTA. We pick the `exchange_failed` code from
  // the backend's union.
  it("renders generic-error banner when the OAuth callback posts a non-Grid error", async () => {
    mockedApi.getTenantSlackBinding.mockResolvedValue(null);
    mockedApi.beginSlackOAuthAuthorize.mockResolvedValue({
      authorizeUrl: "https://slack.com/oauth/v2/authorize?state=abc",
      state: "abc",
    });
    mockedHandoff.mockResolvedValue({
      kind: "error",
      error: "exchange_failed",
      message: "Slack returned invalid_code on oauth.v2.access.",
    });

    const popupStub = { close: vi.fn(), focus: vi.fn(), location: { href: "" } };
    const openSpy = vi
      .spyOn(window, "open")
      .mockReturnValue(popupStub as unknown as Window);

    const { wrapper } = wrap();
    await act(async () => {
      render(<SlackConnectorPanel />, { wrapper });
    });

    await act(async () => {
      fireEvent.click(await screen.findByTestId("slack-panel-install"));
    });

    expect(
      await screen.findByTestId("slack-panel-error-generic"),
    ).toHaveTextContent(/invalid_code/);
    expect(
      screen.getByTestId("slack-panel-error-retry"),
    ).toBeInTheDocument();

    openSpy.mockRestore();
  });

  // "popup-closed" — user cancelled the OAuth window. Render a notice
  // and keep the empty state visible so they can retry.
  it("surfaces a 'popup-closed' notice when the user closes the OAuth window", async () => {
    mockedApi.getTenantSlackBinding.mockResolvedValue(null);
    mockedApi.beginSlackOAuthAuthorize.mockResolvedValue({
      authorizeUrl: "https://slack.com/oauth/v2/authorize?state=abc",
      state: "abc",
    });
    mockedHandoff.mockResolvedValue({ kind: "popup-closed" });

    const popupStub = { close: vi.fn(), focus: vi.fn(), location: { href: "" } };
    const openSpy = vi
      .spyOn(window, "open")
      .mockReturnValue(popupStub as unknown as Window);

    const { wrapper } = wrap();
    await act(async () => {
      render(<SlackConnectorPanel />, { wrapper });
    });

    await act(async () => {
      fireEvent.click(await screen.findByTestId("slack-panel-install"));
    });

    expect(
      await screen.findByTestId("slack-panel-notice-popup-closed"),
    ).toHaveTextContent(/Slack install cancelled/);

    openSpy.mockRestore();
  });

  it("surfaces a 'timed-out' notice when the handoff deadline elapses", async () => {
    mockedApi.getTenantSlackBinding.mockResolvedValue(null);
    mockedApi.beginSlackOAuthAuthorize.mockResolvedValue({
      authorizeUrl: "https://slack.com/oauth/v2/authorize?state=abc",
      state: "abc",
    });
    mockedHandoff.mockResolvedValue({ kind: "timed-out" });

    const popupStub = { close: vi.fn(), focus: vi.fn(), location: { href: "" } };
    const openSpy = vi
      .spyOn(window, "open")
      .mockReturnValue(popupStub as unknown as Window);

    const { wrapper } = wrap();
    await act(async () => {
      render(<SlackConnectorPanel />, { wrapper });
    });

    await act(async () => {
      fireEvent.click(await screen.findByTestId("slack-panel-install"));
    });

    expect(
      await screen.findByTestId("slack-panel-notice-timed-out"),
    ).toHaveTextContent(/didn't complete/);

    openSpy.mockRestore();
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
