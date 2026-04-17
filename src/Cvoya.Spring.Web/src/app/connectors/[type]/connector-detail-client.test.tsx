import { render, screen, waitFor } from "@testing-library/react";
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { beforeEach, describe, expect, it, vi } from "vitest";
import type { ReactNode } from "react";

import type {
  ConnectorTypeResponse,
  UnitConnectorPointerResponse,
  UnitResponse,
} from "@/lib/api/types";

const getConnector =
  vi.fn<(slug: string) => Promise<ConnectorTypeResponse | null>>();
const getConnectorConfigSchema =
  vi.fn<(slug: string) => Promise<unknown | null>>();
const listUnits = vi.fn<() => Promise<UnitResponse[]>>();
const getUnitConnector =
  vi.fn<(id: string) => Promise<UnitConnectorPointerResponse | null>>();

vi.mock("@/lib/api/client", () => ({
  api: {
    getConnector: (slug: string) => getConnector(slug),
    getConnectorConfigSchema: (slug: string) => getConnectorConfigSchema(slug),
    listUnits: () => listUnits(),
    getUnitConnector: (id: string) => getUnitConnector(id),
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

import ConnectorDetailClient from "./connector-detail-client";

function renderClient(slug: string) {
  const client = new QueryClient({
    defaultOptions: {
      queries: { retry: false, gcTime: 0, staleTime: 0 },
      mutations: { retry: false },
    },
  });
  const Wrapper = ({ children }: { children: ReactNode }) => (
    <QueryClientProvider client={client}>{children}</QueryClientProvider>
  );
  return render(<ConnectorDetailClient slugOrId={slug} />, { wrapper: Wrapper });
}

describe("ConnectorDetailClient", () => {
  beforeEach(() => {
    getConnector.mockReset();
    getConnectorConfigSchema.mockReset();
    listUnits.mockReset();
    getUnitConnector.mockReset();
  });

  it("renders breadcrumbs, identity, schema, and bound units", async () => {
    getConnector.mockResolvedValue({
      typeId: "github-id",
      typeSlug: "github",
      displayName: "GitHub",
      description: "Listen to GitHub webhooks.",
      configUrl: "/api/v1/connectors/github/units/{unitId}/config",
      actionsBaseUrl: "/api/v1/connectors/github/actions",
      configSchemaUrl: "/api/v1/connectors/github/config-schema",
    } as ConnectorTypeResponse);
    getConnectorConfigSchema.mockResolvedValue({
      type: "object",
      properties: { owner: { type: "string" } },
    });
    listUnits.mockResolvedValue([
      { id: "u1", name: "alpha", displayName: "Alpha" } as UnitResponse,
      { id: "u2", name: "beta", displayName: "Beta" } as UnitResponse,
    ]);
    getUnitConnector.mockImplementation(async (id) =>
      id === "u1"
        ? ({
            typeId: "github-id",
            typeSlug: "github",
          } as UnitConnectorPointerResponse)
        : null,
    );

    renderClient("github");

    await waitFor(() => {
      expect(screen.getByRole("heading", { name: /GitHub/ })).toBeInTheDocument();
    });

    expect(
      screen.getByRole("link", { name: "Connectors" }),
    ).toHaveAttribute("href", "/connectors");

    const schemaPre = await screen.findByTestId("connector-config-schema");
    expect(schemaPre.textContent).toContain("owner");

    await waitFor(() => {
      expect(screen.getByText("Alpha")).toBeInTheDocument();
    });
    expect(screen.queryByText("Beta")).not.toBeInTheDocument();
    expect(screen.getByText(/Bound units \(1\)/)).toBeInTheDocument();
    expect(
      screen.getByLabelText("Open Alpha unit detail"),
    ).toHaveAttribute("href", "/units/u1");
  });

  it("renders the not-registered state when the connector returns null", async () => {
    getConnector.mockResolvedValue(null);

    renderClient("ghost");

    await waitFor(() => {
      expect(
        screen.getByText(/is not registered on this server/i),
      ).toBeInTheDocument();
    });
    expect(
      screen.getByRole("link", { name: "Connectors" }),
    ).toHaveAttribute("href", "/connectors");
  });

  it("falls back to a hint when the connector advertises no JSON Schema", async () => {
    getConnector.mockResolvedValue({
      typeId: "raw-id",
      typeSlug: "raw",
      displayName: "Raw",
      description: null,
      configUrl: "/api/v1/connectors/raw/units/{unitId}/config",
      actionsBaseUrl: "/api/v1/connectors/raw/actions",
      configSchemaUrl: "/api/v1/connectors/raw/config-schema",
    } as ConnectorTypeResponse);
    getConnectorConfigSchema.mockResolvedValue(null);
    listUnits.mockResolvedValue([]);

    renderClient("raw");

    await waitFor(() => {
      expect(
        screen.getByText(/does not advertise a JSON Schema/i),
      ).toBeInTheDocument();
    });
    expect(screen.queryByTestId("connector-config-schema")).not.toBeInTheDocument();
  });
});
