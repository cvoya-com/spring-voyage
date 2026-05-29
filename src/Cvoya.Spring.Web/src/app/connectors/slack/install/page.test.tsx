import { act, fireEvent, render, screen, waitFor } from "@testing-library/react";
import { beforeEach, describe, expect, it, vi } from "vitest";

// Mock the shared API client before importing the page. The error
// branch checks `err instanceof ApiError`, so the mock exposes the real
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
    api: { installSlackApp: vi.fn() },
  };
});

vi.mock("@connector-slack/slack-oauth-browser", () => ({
  awaitSlackOAuthHandoff: vi.fn(),
  buildSlackOAuthClientState: vi.fn(() => '{"targetOrigin":"https://portal.example"}'),
}));

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
import { awaitSlackOAuthHandoff } from "@connector-slack/slack-oauth-browser";
import SlackInstallWizardPage from "./page";

const mockedApi = vi.mocked(api);
const mockedHandoff = vi.mocked(awaitSlackOAuthHandoff);

function typeConfigToken(value: string): void {
  fireEvent.change(screen.getByTestId("slack-install-config-token"), {
    target: { value },
  });
}

describe("SlackInstallWizardPage", () => {
  beforeEach(() => {
    vi.clearAllMocks();
  });

  it("renders the three fields, the Socket Mode toggle, and both actions", async () => {
    await act(async () => {
      render(<SlackInstallWizardPage />);
    });

    expect(screen.getByTestId("slack-install-config-token")).toBeInTheDocument();
    expect(screen.getByTestId("slack-install-app-name")).toBeInTheDocument();
    expect(screen.getByTestId("slack-install-sv-host")).toBeInTheDocument();
    expect(screen.getByTestId("slack-install-socket-mode")).toBeInTheDocument();
    expect(screen.getByTestId("slack-install-preview")).toBeInTheDocument();
    expect(screen.getByTestId("slack-install-submit")).toBeInTheDocument();
  });

  it("renders the manifest preview from a dry-run", async () => {
    mockedApi.installSlackApp.mockResolvedValue({
      manifestJson: '{"display_information":{"name":"Spring Voyage"}}',
      dryRun: true,
      appId: null,
      authorizeUrl: null,
      state: null,
      writtenSecretNames: [],
    });

    await act(async () => {
      render(<SlackInstallWizardPage />);
    });

    await act(async () => {
      fireEvent.click(screen.getByTestId("slack-install-preview"));
    });

    const preview = await screen.findByTestId("slack-install-manifest-preview");
    expect(preview).toHaveTextContent(/display_information/);
    // The dry-run request carries dryRun: true.
    expect(mockedApi.installSlackApp).toHaveBeenCalledWith(
      expect.objectContaining({ dryRun: true }),
    );
  });

  it("opens the popup, drives the OAuth handoff, and routes to settings on success", async () => {
    mockedApi.installSlackApp.mockResolvedValue({
      manifestJson: '{"display_information":{"name":"Spring Voyage"}}',
      dryRun: false,
      appId: "A0123456789",
      authorizeUrl: "https://slack.com/oauth/v2/authorize?state=st-1",
      state: "st-1",
      writtenSecretNames: ["slack-oauth-client-id"],
    });
    mockedHandoff.mockResolvedValue({ kind: "success" });

    const popupStub = { close: vi.fn(), focus: vi.fn(), location: { href: "" } };
    const openSpy = vi
      .spyOn(window, "open")
      .mockReturnValue(popupStub as unknown as Window);

    await act(async () => {
      render(<SlackInstallWizardPage />);
    });

    typeConfigToken("xoxe.xoxp-test");
    await act(async () => {
      fireEvent.click(screen.getByTestId("slack-install-submit"));
    });

    await waitFor(() => expect(mockPush).toHaveBeenCalledWith("/settings"));
    expect(mockedApi.installSlackApp).toHaveBeenCalledWith(
      expect.objectContaining({ dryRun: false, configToken: "xoxe.xoxp-test" }),
    );
    expect(popupStub.location.href).toBe(
      "https://slack.com/oauth/v2/authorize?state=st-1",
    );
    expect(mockedHandoff).toHaveBeenCalledOnce();

    openSpy.mockRestore();
  });

  it("surfaces an expired-token error without opening OAuth when the token is rejected", async () => {
    mockedApi.installSlackApp.mockRejectedValue(
      new ApiError(502, "Bad Gateway", {
        title: "Slack rejected the app manifest",
        detail:
          "Slack rejected the configuration token (invalid_auth). Generate a fresh one and retry.",
        code: "invalid_auth",
      }),
    );

    const popupStub = { close: vi.fn(), focus: vi.fn(), location: { href: "" } };
    const openSpy = vi
      .spyOn(window, "open")
      .mockReturnValue(popupStub as unknown as Window);

    await act(async () => {
      render(<SlackInstallWizardPage />);
    });

    typeConfigToken("xoxe.expired");
    await act(async () => {
      fireEvent.click(screen.getByTestId("slack-install-submit"));
    });

    const error = await screen.findByTestId("slack-install-error");
    expect(error).toHaveTextContent(/rejected the configuration token/i);
    expect(mockPush).not.toHaveBeenCalled();
    // The popup was opened (synchronously, before the await) but must be
    // closed once the install request rejects.
    expect(popupStub.close).toHaveBeenCalled();

    openSpy.mockRestore();
  });

  it("blocks install with an error when the config token is empty", async () => {
    const openSpy = vi.spyOn(window, "open");

    await act(async () => {
      render(<SlackInstallWizardPage />);
    });

    await act(async () => {
      fireEvent.click(screen.getByTestId("slack-install-submit"));
    });

    expect(await screen.findByTestId("slack-install-error")).toHaveTextContent(
      /Configuration token required/i,
    );
    expect(mockedApi.installSlackApp).not.toHaveBeenCalled();
    expect(openSpy).not.toHaveBeenCalled();

    openSpy.mockRestore();
  });

  it("surfaces a popup-closed outcome as an actionable error", async () => {
    mockedApi.installSlackApp.mockResolvedValue({
      manifestJson: "{}",
      dryRun: false,
      appId: "A1",
      authorizeUrl: "https://slack.com/oauth/v2/authorize?state=st-1",
      state: "st-1",
      writtenSecretNames: [],
    });
    mockedHandoff.mockResolvedValue({ kind: "popup-closed" });

    const popupStub = { close: vi.fn(), focus: vi.fn(), location: { href: "" } };
    const openSpy = vi
      .spyOn(window, "open")
      .mockReturnValue(popupStub as unknown as Window);

    await act(async () => {
      render(<SlackInstallWizardPage />);
    });

    typeConfigToken("xoxe.xoxp-test");
    await act(async () => {
      fireEvent.click(screen.getByTestId("slack-install-submit"));
    });

    expect(await screen.findByTestId("slack-install-error")).toHaveTextContent(
      /didn't complete/i,
    );
    expect(mockPush).not.toHaveBeenCalled();

    openSpy.mockRestore();
  });
});
