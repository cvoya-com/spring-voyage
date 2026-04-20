import { render, screen, waitFor, within } from "@testing-library/react";
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { beforeEach, describe, expect, it, vi } from "vitest";
import type { ReactNode } from "react";

import { expectNoAxeViolations } from "@/test/a11y";
import type {
  CredentialHealthResponse,
  InstalledConnectorResponse,
} from "@/lib/api/types";

const listConnectors = vi.fn<() => Promise<InstalledConnectorResponse[]>>();
const getConnectorCredentialHealth =
  vi.fn<(slug: string) => Promise<CredentialHealthResponse | null>>();

vi.mock("@/lib/api/client", () => ({
  api: {
    listConnectors: () => listConnectors(),
    getConnectorCredentialHealth: (slug: string) =>
      getConnectorCredentialHealth(slug),
  },
}));

vi.mock("next/link", () => ({
  default: ({
    href,
    children,
    ...rest
  }: {
    href: string;
    children: ReactNode;
  } & Record<string, unknown>) => (
    <a href={href} {...rest}>
      {children}
    </a>
  ),
}));

import AdminConnectorsPage from "./page";

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
  return render(<AdminConnectorsPage />, { wrapper: Wrapper });
}

function makeConnector(
  overrides: Partial<InstalledConnectorResponse> = {},
): InstalledConnectorResponse {
  return {
    typeId: "github-id",
    typeSlug: "github",
    displayName: "GitHub",
    description: "Listen to GitHub webhooks.",
    configUrl: "/api/v1/connectors/github/units/{unitId}/config",
    actionsBaseUrl: "/api/v1/connectors/github/actions",
    configSchemaUrl: "/api/v1/connectors/github/config-schema",
    installedAt: "2026-04-01T00:00:00Z",
    updatedAt: "2026-04-10T00:00:00Z",
    config: null,
    ...overrides,
  } as InstalledConnectorResponse;
}

describe("AdminConnectorsPage", () => {
  beforeEach(() => {
    listConnectors.mockReset();
    getConnectorCredentialHealth.mockReset();
  });

  it("renders installed connectors with credential health and CLI callout", async () => {
    listConnectors.mockResolvedValue([
      makeConnector(),
      makeConnector({
        typeId: "slack-id",
        typeSlug: "slack",
        displayName: "Slack",
        description: "Slack messages.",
      }),
    ]);
    getConnectorCredentialHealth.mockImplementation(async (slug) => ({
      subjectId: slug,
      secretName: "default",
      status: slug === "github" ? "Valid" : "Expired",
      lastError: slug === "github" ? null : "installation expired",
      lastChecked: "2026-04-18T12:00:00Z",
    }));

    renderPage();

    await waitFor(() => {
      expect(screen.getByText("GitHub")).toBeInTheDocument();
    });
    expect(screen.getByText("Slack")).toBeInTheDocument();

    await waitFor(() => {
      expect(
        screen.getByTestId("admin-connector-health-github"),
      ).toHaveTextContent("Valid");
    });
    expect(
      screen.getByTestId("admin-connector-health-slack"),
    ).toHaveTextContent("Expired");
    expect(screen.getByText(/installation expired/)).toBeInTheDocument();

    expect(
      screen.getByText(/Read-only view — mutations go through the CLI\./i),
    ).toBeInTheDocument();
    expect(screen.getByText(/spring connector/i)).toBeInTheDocument();
  });

  it("renders the empty state when no connectors are installed", async () => {
    listConnectors.mockResolvedValue([]);

    renderPage();

    await waitFor(() => {
      expect(
        screen.getByText(/No connectors installed on this tenant\./i),
      ).toBeInTheDocument();
    });
  });

  it("renders 'No signal yet' when the credential-health row is 404", async () => {
    listConnectors.mockResolvedValue([makeConnector({ typeSlug: "webhook" })]);
    getConnectorCredentialHealth.mockResolvedValue(null);

    renderPage();

    await waitFor(() => {
      expect(
        screen.getByTestId("admin-connector-health-webhook"),
      ).toHaveTextContent(/No signal yet/i);
    });
  });

  it("exposes no mutation controls (no install/uninstall/configure buttons)", async () => {
    listConnectors.mockResolvedValue([makeConnector()]);
    getConnectorCredentialHealth.mockResolvedValue(null);

    const { container } = renderPage();

    await waitFor(() => {
      expect(screen.getByText("GitHub")).toBeInTheDocument();
    });

    const buttons = within(container).queryAllByRole("button");
    expect(buttons).toHaveLength(0);

    expect(container.querySelector("form")).toBeNull();
    expect(container.querySelector("input")).toBeNull();
    expect(container.querySelector("select")).toBeNull();
    expect(container.querySelector("textarea")).toBeNull();
  });

  it("is axe-clean with populated data", async () => {
    listConnectors.mockResolvedValue([makeConnector()]);
    getConnectorCredentialHealth.mockResolvedValue({
      subjectId: "github",
      secretName: "default",
      status: "Valid",
      lastError: null,
      lastChecked: "2026-04-18T12:00:00Z",
    });

    const { container } = renderPage();
    await waitFor(() => {
      expect(screen.getByText("GitHub")).toBeInTheDocument();
    });
    await expectNoAxeViolations(container);
  });
});
