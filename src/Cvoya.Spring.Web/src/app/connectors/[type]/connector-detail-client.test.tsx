import { render, screen, waitFor } from "@testing-library/react";
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { beforeEach, describe, expect, it, vi } from "vitest";
import type { ReactNode } from "react";

import { ApiError } from "@/lib/api/client";
import type {
  InstalledConnectorResponse,
  ConnectorUnitBindingResponse,
} from "@/lib/api/types";

const getConnector =
  vi.fn<(slug: string) => Promise<InstalledConnectorResponse | null>>();
const getConnectorConfigSchema =
  vi.fn<(slug: string) => Promise<unknown | null>>();
const listConnectorBindings =
  vi.fn<(slugOrId: string) => Promise<ConnectorUnitBindingResponse[]>>();

vi.mock("@/lib/api/client", async () => {
  const actual =
    await vi.importActual<typeof import("@/lib/api/client")>("@/lib/api/client");
  return {
    ...actual,
    api: {
      getConnector: (slug: string) => getConnector(slug),
      getConnectorConfigSchema: (slug: string) => getConnectorConfigSchema(slug),
      listConnectorBindings: (slug: string) => listConnectorBindings(slug),
    },
  };
});

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
    listConnectorBindings.mockReset();
  });

  it("renders breadcrumbs, identity, schema, and bound units", async () => {
    getConnector.mockResolvedValue({
      typeId: "github-id",
      typeSlug: "github",
      displayName: "GitHub",
      description: "Listen to GitHub webhooks.",
      configUrl: "/api/v1/tenant/connectors/github/units/{unitId}/config",
      actionsBaseUrl: "/api/v1/tenant/connectors/github/actions",
      configSchemaUrl: "/api/v1/tenant/connectors/github/config-schema",
    } as InstalledConnectorResponse);
    getConnectorConfigSchema.mockResolvedValue({
      type: "object",
      properties: { owner: { type: "string" } },
    });
    // Bulk endpoint (#520) returns only the units bound to this connector
    // type, so the client no longer post-filters a full unit list.
    listConnectorBindings.mockResolvedValue([
      {
        unitId: "u1",
        unitName: "alpha",
        unitDisplayName: "Alpha",
        typeId: "github-id",
        typeSlug: "github",
        configUrl: "/api/v1/tenant/connectors/github/units/u1/config",
        actionsBaseUrl: "/api/v1/tenant/connectors/github/actions",
      } as ConnectorUnitBindingResponse,
    ]);

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
    ).toHaveAttribute("href", "/explorer/units/u1?tab=Overview");
  });

  it("renders the not-installed state when the connector returns null", async () => {
    getConnector.mockResolvedValue(null);

    renderClient("ghost");

    await waitFor(() => {
      expect(
        screen.getByText(/is not installed on the current/i),
      ).toBeInTheDocument();
    });
    expect(
      screen.getByRole("link", { name: "Connectors" }),
    ).toHaveAttribute("href", "/connectors");
  });

  it("renders translated copy when the connector load throws ProblemDetails (#2163)", async () => {
    getConnector.mockRejectedValue(
      new ApiError(404, "Not Found", {
        type: "https://cvoya.com/problems/unit-not-found",
        title: "Not Found",
        status: 404,
        detail: "UnitNotFound: connector deleted.",
        code: "UnitNotFound",
        traceId: "00-conn-detail",
      }),
    );

    renderClient("github");

    // Friendly title + next step are visible immediately (the lead copy
    // for this surface is now the translator output, not the raw envelope).
    await waitFor(() => {
      expect(screen.getByText(/Unit not found\./)).toBeInTheDocument();
    });
    expect(
      screen.getByText(/It may have been deleted\. Refresh the page/),
    ).toBeInTheDocument();
    // No "API error 404" prose anywhere — the canonical leak the
    // translator was introduced to fix (#2157) must stay absent.
    expect(screen.queryByText(/API error 404/)).not.toBeInTheDocument();
  });

  it("falls back to a hint when the connector advertises no JSON Schema", async () => {
    getConnector.mockResolvedValue({
      typeId: "raw-id",
      typeSlug: "raw",
      displayName: "Raw",
      // Post-#714 Description is non-nullable on the installed envelope;
      // use an empty string for the "no description" path.
      description: "",
      configUrl: "/api/v1/tenant/connectors/raw/units/{unitId}/config",
      actionsBaseUrl: "/api/v1/tenant/connectors/raw/actions",
      configSchemaUrl: "/api/v1/tenant/connectors/raw/config-schema",
      installedAt: "2026-04-20T00:00:00Z",
      updatedAt: "2026-04-20T00:00:00Z",
      config: null,
    } as InstalledConnectorResponse);
    getConnectorConfigSchema.mockResolvedValue(null);
    listConnectorBindings.mockResolvedValue([]);

    renderClient("raw");

    await waitFor(() => {
      expect(
        screen.getByText(/does not advertise a JSON Schema/i),
      ).toBeInTheDocument();
    });
    expect(screen.queryByTestId("connector-config-schema")).not.toBeInTheDocument();
  });
});
