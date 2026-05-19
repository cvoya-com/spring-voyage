import { act, fireEvent, render, screen, waitFor } from "@testing-library/react";
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { beforeEach, describe, expect, it, vi } from "vitest";

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
      listTenantSecrets: vi.fn(),
      deleteTenantSecret: vi.fn(),
    },
  };
});

vi.mock("@/components/ui/toast", async () => ({
  useToast: () => ({ toast: vi.fn() }),
}));

import { api } from "@/lib/api/client";
import { OrphanSecretsPanel } from "./orphan-secrets";

const mocked = vi.mocked(api);

function renderWithClient(node: React.ReactNode) {
  const client = new QueryClient({
    defaultOptions: { queries: { retry: false } },
  });
  return render(
    <QueryClientProvider client={client}>{node}</QueryClientProvider>,
  );
}

describe("OrphanSecretsPanel", () => {
  beforeEach(() => {
    vi.resetAllMocks();
  });

  it("renders the empty state when no binding-scoped secrets exist", async () => {
    mocked.listTenantSecrets.mockResolvedValue({
      secrets: [
        {
          name: "openai/api-key",
          scope: "Tenant",
          createdAt: "2026-01-01T00:00:00Z",
        },
      ],
    });

    await act(async () => {
      renderWithClient(<OrphanSecretsPanel />);
    });

    await waitFor(() =>
      expect(
        screen.getByTestId("user-identity-orphan-secrets-empty"),
      ).toBeInTheDocument(),
    );
  });

  // ADR-0047 §5: orphan secrets follow the `binding/<id>/<slug>/pat`
  // naming convention. The panel lists every match and offers a
  // Forget button per row.
  it("lists matching binding-scoped secrets and exposes a Forget affordance (ADR-0047 §5)", async () => {
    mocked.listTenantSecrets.mockResolvedValue({
      secrets: [
        {
          name: "binding/abcdef0123456789abcdef0123456789/github/pat",
          scope: "Tenant",
          createdAt: "2026-01-01T00:00:00Z",
        },
        {
          name: "openai/api-key",
          scope: "Tenant",
          createdAt: "2026-01-01T00:00:00Z",
        },
      ],
    });

    await act(async () => {
      renderWithClient(<OrphanSecretsPanel />);
    });

    await waitFor(() =>
      expect(
        screen.getByTestId("user-identity-orphan-secrets-list"),
      ).toBeInTheDocument(),
    );
    expect(
      screen.getByText(
        "binding/abcdef0123456789abcdef0123456789/github/pat",
      ),
    ).toBeInTheDocument();
    // Unrelated tenant secrets must not appear.
    expect(screen.queryByText("openai/api-key")).not.toBeInTheDocument();
  });

  it("deletes through the API client when the operator confirms the Forget action", async () => {
    mocked.listTenantSecrets.mockResolvedValue({
      secrets: [
        {
          name: "binding/abcdef0123456789abcdef0123456789/github/pat",
          scope: "Tenant",
          createdAt: "2026-01-01T00:00:00Z",
        },
      ],
    });
    mocked.deleteTenantSecret.mockResolvedValue(undefined);

    await act(async () => {
      renderWithClient(<OrphanSecretsPanel />);
    });

    await waitFor(() =>
      expect(
        screen.getByTestId("user-identity-orphan-secrets-list"),
      ).toBeInTheDocument(),
    );

    await act(async () => {
      fireEvent.click(
        screen.getByTestId(
          "user-identity-orphan-secret-forget-binding/abcdef0123456789abcdef0123456789/github/pat",
        ),
      );
    });

    // The ConfirmDialog renders a destructive primary action button —
    // pick it via the dialog footer's role so we don't collide with
    // the list-row Forget buttons.
    await act(async () => {
      const buttons = screen.getAllByRole("button", { name: /^forget$/i });
      // The last "Forget" button is the dialog's confirm action; the
      // earlier ones are the list-row triggers.
      fireEvent.click(buttons[buttons.length - 1]!);
    });

    await waitFor(() =>
      expect(mocked.deleteTenantSecret).toHaveBeenCalledWith(
        "binding/abcdef0123456789abcdef0123456789/github/pat",
      ),
    );
  });
});
