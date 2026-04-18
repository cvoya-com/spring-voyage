import { render, screen, waitFor } from "@testing-library/react";
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { beforeEach, describe, expect, it, vi } from "vitest";
import type { ReactNode } from "react";

import type {
  CostSummaryResponse,
  UnitMembershipResponse,
} from "@/lib/api/types";

const getUnitCost =
  vi.fn<
    (
      id: string,
      range?: { from?: string; to?: string },
    ) => Promise<CostSummaryResponse>
  >();
const getAgentCost =
  vi.fn<
    (
      id: string,
      range?: { from?: string; to?: string },
    ) => Promise<CostSummaryResponse>
  >();
const listUnitMemberships =
  vi.fn<(id: string) => Promise<UnitMembershipResponse[]>>();

vi.mock("@/lib/api/client", () => ({
  api: {
    getUnitCost: (id: string, range?: { from?: string; to?: string }) =>
      getUnitCost(id, range),
    getAgentCost: (id: string, range?: { from?: string; to?: string }) =>
      getAgentCost(id, range),
    listUnitMemberships: (id: string) => listUnitMemberships(id),
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

import { CostsTab } from "./costs-tab";

function renderTab(id: string) {
  const client = new QueryClient({
    defaultOptions: {
      queries: { retry: false, gcTime: 0, staleTime: 0 },
    },
  });
  const Wrapper = ({ children }: { children: ReactNode }) => (
    <QueryClientProvider client={client}>{children}</QueryClientProvider>
  );
  return render(<CostsTab unitId={id} />, { wrapper: Wrapper });
}

function makeSummary(
  overrides: Partial<CostSummaryResponse> = {},
): CostSummaryResponse {
  return {
    totalCost: 1,
    totalInputTokens: 10,
    totalOutputTokens: 5,
    recordCount: 1,
    workCost: 1,
    initiativeCost: 0,
    from: "2026-04-01T00:00:00Z",
    to: "2026-04-17T00:00:00Z",
    ...overrides,
  } as CostSummaryResponse;
}

function makeMembership(
  agentAddress: string,
): UnitMembershipResponse {
  return {
    unitId: "engineering",
    agentAddress,
    model: null,
    specialty: null,
    enabled: true,
    executionMode: null,
    createdAt: "2026-04-01T00:00:00Z",
    updatedAt: "2026-04-01T00:00:00Z",
  } as UnitMembershipResponse;
}

describe("CostsTab", () => {
  beforeEach(() => {
    getUnitCost.mockReset();
    getAgentCost.mockReset();
    listUnitMemberships.mockReset();
  });

  it("renders three window bars (24h / 7d / 30d) for the unit", async () => {
    getUnitCost.mockResolvedValue(makeSummary({ totalCost: 3.21 }));
    listUnitMemberships.mockResolvedValue([]);

    renderTab("engineering");

    await waitFor(() => {
      expect(getUnitCost).toHaveBeenCalledTimes(3);
    });
    expect(
      await screen.findByTestId("unit-costs-24h"),
    ).toBeInTheDocument();
    expect(screen.getByTestId("unit-costs-7d")).toBeInTheDocument();
    expect(screen.getByTestId("unit-costs-30d")).toBeInTheDocument();
  });

  it("lists per-agent breakdown rows sorted by cost desc", async () => {
    getUnitCost.mockResolvedValue(makeSummary());
    listUnitMemberships.mockResolvedValue([
      makeMembership("engineering/ada"),
      makeMembership("engineering/grace"),
    ]);
    getAgentCost.mockImplementation((id) => {
      if (id === "engineering/ada") {
        return Promise.resolve(makeSummary({ totalCost: 5 }));
      }
      if (id === "engineering/grace") {
        return Promise.resolve(makeSummary({ totalCost: 10 }));
      }
      return Promise.resolve(makeSummary({ totalCost: 0 }));
    });

    renderTab("engineering");

    const breakdown = await screen.findByTestId(
      "unit-costs-agent-breakdown",
    );
    await waitFor(() => {
      // Both rows rendered.
      const links = breakdown.querySelectorAll("a");
      expect(links.length).toBe(2);
    });
    const links = Array.from(breakdown.querySelectorAll("a"));
    // grace (10) should come before ada (5) — sorted desc.
    expect(links[0]).toHaveTextContent("engineering/grace");
    expect(links[1]).toHaveTextContent("engineering/ada");
  });

  it("renders a friendly empty state when the unit has no memberships", async () => {
    getUnitCost.mockResolvedValue(makeSummary());
    listUnitMemberships.mockResolvedValue([]);

    renderTab("engineering");

    await waitFor(() => {
      expect(
        screen.getByText(/no agents belong to this unit/i),
      ).toBeInTheDocument();
    });
  });
});
