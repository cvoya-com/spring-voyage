/**
 * Unit tests for `/budgets` (SURF-reskin-budgets, #856). The page is a
 * visibility anchor — every mutation still lands on
 * `/analytics/costs` or the unit Policies → Cost tab. These tests
 * assert that the surface correctly surfaces tenant + per-unit spend
 * and cross-links to the canonical editors.
 */

import { render, screen, waitFor } from "@testing-library/react";
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { beforeEach, describe, expect, it, vi } from "vitest";
import type { ReactNode } from "react";

import type {
  BudgetResponse,
  CostDashboardSummary,
  CostSummaryResponse,
  TenantCostTimeseriesResponse,
  UnitDashboardSummary,
} from "@/lib/api/types";

const getTenantBudget = vi.fn<() => Promise<BudgetResponse | null>>();
const getDashboardCosts = vi.fn<() => Promise<CostDashboardSummary>>();
const getDashboardUnits = vi.fn<() => Promise<UnitDashboardSummary[]>>();
const getTenantCost = vi.fn<() => Promise<CostSummaryResponse | null>>();
const getTenantCostTimeseries =
  vi.fn<() => Promise<TenantCostTimeseriesResponse | null>>();

vi.mock("@/lib/api/client", () => ({
  api: {
    getTenantBudget: () => getTenantBudget(),
    getDashboardCosts: () => getDashboardCosts(),
    getDashboardUnits: () => getDashboardUnits(),
    getTenantCost: () => getTenantCost(),
    getTenantCostTimeseries: () => getTenantCostTimeseries(),
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

import BudgetsIndexPage from "./page";

function renderPage() {
  const client = new QueryClient({
    defaultOptions: {
      queries: { retry: false, gcTime: 0, staleTime: 0 },
    },
  });
  const Wrapper = ({ children }: { children: ReactNode }) => (
    <QueryClientProvider client={client}>{children}</QueryClientProvider>
  );
  return render(<BudgetsIndexPage />, { wrapper: Wrapper });
}

function makeUnit(
  overrides: Partial<UnitDashboardSummary> = {},
): UnitDashboardSummary {
  return {
    name: "alpha",
    displayName: "Alpha",
    registeredAt: "2026-04-01T00:00:00Z",
    status: "Running",
    ...overrides,
  } as UnitDashboardSummary;
}

describe("/budgets", () => {
  beforeEach(() => {
    getTenantBudget.mockReset();
    getDashboardCosts.mockReset();
    getDashboardUnits.mockReset();
    getTenantCost.mockReset();
    getTenantCostTimeseries.mockReset();
    getTenantCost.mockResolvedValue({ totalCost: 0, breakdowns: [] } as unknown as CostSummaryResponse);
    // Default to the empty-series shape — the `/budgets` sparkline
    // renders "no line" rather than a flat zero for empty tenants.
    getTenantCostTimeseries.mockResolvedValue({
      from: "2026-03-22T00:00:00Z",
      to: "2026-04-21T00:00:00Z",
      bucket: "1d",
      series: [],
    });
  });

  it("renders the tenant budget card with cap and utilisation", async () => {
    getTenantBudget.mockResolvedValue({ dailyBudget: 50 } as BudgetResponse);
    getDashboardCosts.mockResolvedValue({
      totalCost: 18.24,
      costsBySource: [],
      periodStart: null,
      periodEnd: null,
    });
    getDashboardUnits.mockResolvedValue([]);

    renderPage();

    await waitFor(() => {
      // 18.24 / 50 = 36%. The card is rendered synchronously but the
      // pct text only lands once the tenant budget + dashboard cost
      // queries resolve.
      expect(
        screen.getByTestId("budgets-tenant-pct"),
      ).toHaveTextContent(/36% of daily cap/i);
    });
    const editLink = screen.getByTestId("budgets-edit-link");
    expect(editLink).toHaveAttribute("href", "/analytics/costs");
  });

  it("renders a per-unit row with drill-down into the Explorer Policies tab", async () => {
    getTenantBudget.mockResolvedValue({ dailyBudget: 100 } as BudgetResponse);
    getDashboardCosts.mockResolvedValue({
      totalCost: 80,
      costsBySource: [{ source: "unit://alpha", totalCost: 70 }],
      periodStart: null,
      periodEnd: null,
    });
    getDashboardUnits.mockResolvedValue([makeUnit()]);

    renderPage();

    await waitFor(() => {
      const row = screen.getByTestId("budgets-unit-row-alpha");
      expect(row).toBeInTheDocument();
      expect(row).toHaveAttribute(
        "href",
        "/units?node=alpha&tab=policies",
      );
    });
    // 70 / 100 = 70% — should be the warning variant.
    expect(screen.getByText(/70% of cap/i)).toBeInTheDocument();
  });

  it("shows the empty state when no units exist", async () => {
    getTenantBudget.mockResolvedValue(null);
    getDashboardCosts.mockResolvedValue({
      totalCost: 0,
      costsBySource: [],
      periodStart: null,
      periodEnd: null,
    });
    getDashboardUnits.mockResolvedValue([]);

    renderPage();

    await waitFor(() => {
      expect(screen.getByText(/No units yet/i)).toBeInTheDocument();
    });
    const create = screen.getByRole("link", { name: /Create a unit/i });
    expect(create).toHaveAttribute("href", "/units/create");
  });

  it("renders the secondary KPI strip with the tenant cap and 24h spend", async () => {
    getTenantBudget.mockResolvedValue({ dailyBudget: 42 } as BudgetResponse);
    getDashboardCosts.mockResolvedValue({
      totalCost: 12.5,
      costsBySource: [],
      periodStart: null,
      periodEnd: null,
    });
    getDashboardUnits.mockResolvedValue([]);

    renderPage();

    await waitFor(() => {
      expect(screen.getByText(/Tenant cap/i)).toBeInTheDocument();
    });
    // KPI values use formatCost → "$42.00", "$12.50". Wait until the
    // StatCard renders after the queries resolve.
    await waitFor(() => {
      expect(screen.getAllByText(/\$42\.00/).length).toBeGreaterThan(0);
    });
    expect(screen.getAllByText(/\$12\.50/).length).toBeGreaterThan(0);
  });

  it("renders the sparkline from the tenant cost time-series endpoint", async () => {
    // Non-empty series — the inline BudgetSparkline should render. The
    // series length + contents don't matter for this assertion; we only
    // need to confirm the hook data flows to the DOM via the
    // `data-testid="budgets-sparkline"` selector.
    getTenantBudget.mockResolvedValue({ dailyBudget: 50 } as BudgetResponse);
    getDashboardCosts.mockResolvedValue({
      totalCost: 20,
      costsBySource: [],
      periodStart: null,
      periodEnd: null,
    });
    getDashboardUnits.mockResolvedValue([]);
    getTenantCostTimeseries.mockResolvedValue({
      from: "2026-03-22T00:00:00Z",
      to: "2026-04-21T00:00:00Z",
      bucket: "1d",
      series: [
        { t: "2026-03-22T00:00:00Z", cost: 0.1 },
        { t: "2026-03-23T00:00:00Z", cost: 0.0 },
        { t: "2026-03-24T00:00:00Z", cost: 0.25 },
      ],
    });

    renderPage();

    await waitFor(() => {
      expect(
        screen.getByTestId("budgets-sparkline"),
      ).toBeInTheDocument();
    });
  });
});
