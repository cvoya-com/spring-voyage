import { act, fireEvent, render, screen, waitFor } from "@testing-library/react";
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { beforeEach, describe, expect, it, vi } from "vitest";

// Mock the API client + toast/router surface used by the card.
vi.mock("@/lib/api/client", async () => {
  class MockApiError extends Error {
    constructor(
      public readonly status: number,
      public readonly statusText: string,
      public readonly body: unknown,
    ) {
      super(`API error ${status}: ${statusText}`);
      this.name = "ApiError";
    }
  }
  return {
    ApiError: MockApiError,
    api: {
      getGitHubUserConfigSchema: vi.fn(),
      upsertTenantUserIdentity: vi.fn(),
      removeTenantUserIdentity: vi.fn(),
      beginGitHubOAuthAuthorize: vi.fn(),
    },
  };
});

vi.mock("@/components/ui/toast", async () => ({
  useToast: () => ({ toast: vi.fn() }),
}));

import { api } from "@/lib/api/client";
import { ConnectorIdentityCard } from "./connector-card";

const mocked = vi.mocked(api);

const GITHUB_SCHEMA = {
  $schema: "https://json-schema.org/draft/2020-12/schema",
  title: "GitHubUserConfig",
  type: "object",
  required: ["username"],
  properties: {
    username: {
      type: "string",
      description: "GitHub login (without leading @).",
    },
    display_handle: {
      type: ["string", "null"],
      description: "Optional human-friendly rendering.",
    },
  },
};

function renderWithClient(node: React.ReactNode) {
  const client = new QueryClient({
    defaultOptions: { queries: { retry: false } },
  });
  return render(
    <QueryClientProvider client={client}>{node}</QueryClientProvider>,
  );
}

describe("ConnectorIdentityCard", () => {
  beforeEach(() => {
    vi.resetAllMocks();
    mocked.getGitHubUserConfigSchema.mockResolvedValue(GITHUB_SCHEMA);
  });

  // ADR-0047 §4: the card reads the connector's user-config schema and
  // renders one input per declared field. For GitHub today that's
  // `username` (required) and `display_handle` (optional).
  it("renders form fields from the connector's user-config schema (ADR-0047 §4)", async () => {
    await act(async () => {
      renderWithClient(
        <ConnectorIdentityCard
          tenantUserId="00000000-0000-0000-0000-000000000001"
          connectorSlug="github"
          connectorDisplayName="GitHub"
          identity={null}
        />,
      );
    });

    await waitFor(() =>
      expect(
        screen.getByTestId("user-identity-input-github-username"),
      ).toBeInTheDocument(),
    );
    expect(
      screen.getByTestId("user-identity-input-github-display_handle"),
    ).toBeInTheDocument();
  });

  it("disables Save until the required username field is filled", async () => {
    await act(async () => {
      renderWithClient(
        <ConnectorIdentityCard
          tenantUserId="00000000-0000-0000-0000-000000000001"
          connectorSlug="github"
          connectorDisplayName="GitHub"
          identity={null}
        />,
      );
    });

    await waitFor(() =>
      expect(screen.getByTestId("user-identity-save-github")).toBeDisabled(),
    );

    await act(async () => {
      fireEvent.change(
        screen.getByTestId("user-identity-input-github-username"),
        { target: { value: "octocat" } },
      );
    });

    expect(screen.getByTestId("user-identity-save-github")).not.toBeDisabled();
  });

  it("upserts the identity through the API client on save", async () => {
    mocked.upsertTenantUserIdentity.mockResolvedValue({
      tenantUserId: "00000000-0000-0000-0000-000000000001",
      connectorId: "github",
      username: "octocat",
      displayHandle: null,
      createdAt: "2026-01-01T00:00:00Z",
      updatedAt: "2026-01-01T00:00:00Z",
    });

    await act(async () => {
      renderWithClient(
        <ConnectorIdentityCard
          tenantUserId="00000000-0000-0000-0000-000000000001"
          connectorSlug="github"
          connectorDisplayName="GitHub"
          identity={null}
        />,
      );
    });

    await waitFor(() =>
      expect(
        screen.getByTestId("user-identity-input-github-username"),
      ).toBeInTheDocument(),
    );

    await act(async () => {
      fireEvent.change(
        screen.getByTestId("user-identity-input-github-username"),
        { target: { value: "octocat" } },
      );
    });

    await act(async () => {
      fireEvent.click(screen.getByTestId("user-identity-save-github"));
    });

    await waitFor(() =>
      expect(mocked.upsertTenantUserIdentity).toHaveBeenCalledWith(
        "00000000-0000-0000-0000-000000000001",
        expect.objectContaining({
          connectorId: "github",
          username: "octocat",
          displayHandle: null,
        }),
      ),
    );
  });

  // ADR-0047 §13: the user-identity intent opens the OAuth popup and
  // the callback handoff populates the username field with the
  // OAuth-supplied `login`.
  it("auto-fills the username field from the OAuth callback login (ADR-0047 §13)", async () => {
    mocked.beginGitHubOAuthAuthorize.mockResolvedValue({
      authorizeUrl: "https://github.com/login/oauth/authorize?state=fresh",
      state: "fresh",
    });
    const popup = {
      focus: vi.fn(),
      close: vi.fn(),
      closed: false,
      location: { href: "" },
    } as unknown as Window;
    const open = vi.spyOn(window, "open").mockReturnValue(popup);

    try {
      await act(async () => {
        renderWithClient(
          <ConnectorIdentityCard
            tenantUserId="00000000-0000-0000-0000-000000000001"
            connectorSlug="github"
            connectorDisplayName="GitHub"
            identity={null}
          />,
        );
      });

      await waitFor(() =>
        expect(
          screen.getByTestId("user-identity-github-authorize-button"),
        ).toBeInTheDocument(),
      );

      await act(async () => {
        fireEvent.click(
          screen.getByTestId("user-identity-github-authorize-button"),
        );
      });

      await waitFor(() =>
        expect(mocked.beginGitHubOAuthAuthorize).toHaveBeenCalledWith(
          expect.objectContaining({
            intent: "user-identity",
            tenantUserId: "00000000-0000-0000-0000-000000000001",
            bindingId: expect.stringMatching(/^[0-9a-f]{32}$/),
          }),
        ),
      );

      await act(async () => {
        window.dispatchEvent(
          new MessageEvent("message", {
            origin: window.location.origin,
            data: {
              type: "spring-voyage:github-oauth-session",
              sessionId: "sess-ui",
              login: "octocat",
            },
          }),
        );
      });

      await waitFor(() => {
        const usernameInput = screen.getByTestId(
          "user-identity-input-github-username",
        ) as HTMLInputElement;
        expect(usernameInput.value).toBe("octocat");
      });
    } finally {
      open.mockRestore();
    }
  });

  it("renders the existing identity read-only with edit + remove affordances", async () => {
    await act(async () => {
      renderWithClient(
        <ConnectorIdentityCard
          tenantUserId="00000000-0000-0000-0000-000000000001"
          connectorSlug="github"
          connectorDisplayName="GitHub"
          identity={{
            tenantUserId: "00000000-0000-0000-0000-000000000001",
            connectorId: "github",
            username: "octocat",
            displayHandle: "Octocat",
            createdAt: "2026-01-01T00:00:00Z",
            updatedAt: "2026-01-01T00:00:00Z",
          }}
        />,
      );
    });

    await waitFor(() =>
      expect(
        screen.getByTestId("user-identity-readonly-github"),
      ).toBeInTheDocument(),
    );
    expect(
      screen.getByTestId("user-identity-edit-github"),
    ).toBeInTheDocument();
    expect(
      screen.getByTestId("user-identity-remove-github"),
    ).toBeInTheDocument();
  });
});
