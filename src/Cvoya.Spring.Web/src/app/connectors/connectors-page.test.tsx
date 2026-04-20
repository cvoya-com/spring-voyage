import { render, screen, waitFor } from "@testing-library/react";
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { beforeEach, describe, expect, it, vi } from "vitest";
import type { ReactNode } from "react";

import type { InstalledConnectorResponse } from "@/lib/api/types";

const listConnectors = vi.fn<() => Promise<InstalledConnectorResponse[]>>();

vi.mock("@/lib/api/client", () => ({
  api: {
    listConnectors: () => listConnectors(),
  },
}));

vi.mock("next/link", () => ({
  default: ({
    href,
    children,
    ...rest
  }: {
    href: string;
    children: React.ReactNode;
  } & Record<string, unknown>) => (
    <a href={href} {...rest}>
      {children}
    </a>
  ),
}));

import ConnectorsListPage from "./page";

function renderPage() {
  const client = new QueryClient({
    defaultOptions: {
      queries: { retry: false, gcTime: 0, staleTime: 0 },
      mutations: { retry: false },
    },
  });
  const Wrapper = ({ children }: { children: ReactNode }) => (
    <QueryClientProvider client={client}>{children}</QueryClientProvider>
  );
  return render(<ConnectorsListPage />, { wrapper: Wrapper });
}

describe("ConnectorsListPage", () => {
  beforeEach(() => {
    listConnectors.mockReset();
  });

  it("renders one card per installed connector with a link to its detail page", async () => {
    listConnectors.mockResolvedValue([
      {
        typeId: "github-id",
        typeSlug: "github",
        displayName: "GitHub",
        description: "Listen to GitHub webhooks.",
        configUrl: "/api/v1/connectors/github/units/{unitId}/config",
        actionsBaseUrl: "/api/v1/connectors/github/actions",
        configSchemaUrl: "/api/v1/connectors/github/config-schema",
      } as InstalledConnectorResponse,
      {
        typeId: "slack-id",
        typeSlug: "slack",
        displayName: "Slack",
        description: "Slack messages.",
        configUrl: "/api/v1/connectors/slack/units/{unitId}/config",
        actionsBaseUrl: "/api/v1/connectors/slack/actions",
        configSchemaUrl: "/api/v1/connectors/slack/config-schema",
      } as InstalledConnectorResponse,
    ]);

    renderPage();

    await waitFor(() => {
      expect(screen.getByText("GitHub")).toBeInTheDocument();
    });
    expect(screen.getByText("Slack")).toBeInTheDocument();

    const githubLink = screen
      .getByLabelText("Open connector GitHub")
      .closest("a");
    expect(githubLink).toHaveAttribute("href", "/connectors/github");
  });

  it("renders the empty state when no connectors are installed on the tenant", async () => {
    listConnectors.mockResolvedValue([]);

    renderPage();

    await waitFor(() => {
      expect(
        screen.getByText(/No connectors installed on this tenant\./i),
      ).toBeInTheDocument();
    });
    const packagesLink = screen.getByRole("link", { name: /Packages/i });
    expect(packagesLink).toHaveAttribute("href", "/packages");
  });
});
