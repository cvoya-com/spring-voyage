import { render, screen, waitFor } from "@testing-library/react";
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { beforeEach, describe, expect, it, vi } from "vitest";
import type { ReactNode } from "react";

import type {
  AgentDashboardSummary,
  BudgetResponse,
  CostDashboardSummary,
  TenantCostTimeseriesResponse,
} from "@/lib/api/types";

const getTenantBudget = vi.fn<() => Promise<BudgetResponse>>();
const getDashboardCosts = vi.fn<() => Promise<CostDashboardSummary>>();
const getDashboardAgents = vi.fn<() => Promise<AgentDashboardSummary[]>>();
const getAgentBudget = vi.fn<(id: string) => Promise<BudgetResponse>>();
const setTenantBudget = vi.fn();
const getTenantCostTimeseries = vi.fn<() => Promise<TenantCostTimeseriesResponse>>();

vi.mock("@/lib/api/client", () => ({
  api: {
    getTenantBudget: () => getTenantBudget(),
    getDashboardCosts: () => getDashboardCosts(),
    getDashboardAgents: () => getDashboardAgents(),
    getAgentBudget: (id: string) => getAgentBudget(id),
    setTenantBudget: (...args: unknown[]) => setTenantBudget(...args),
    getTenantCostTimeseries: () => getTenantCostTimeseries(),
  },
}));

const toastMock = vi.fn();
vi.mock("@/components/ui/toast", () => ({
  useToast: () => ({ toast: toastMock }),
}));

// The page now reads scope + window from the URL via next/navigation.
// Stub both so the test runs in a jsdom environment that has no
// App Router mounted.
vi.mock("next/navigation", () => ({
  useRouter: () => ({ replace: vi.fn(), push: vi.fn() }),
  // #1053: `useAnalyticsFilters` now reads `usePathname()` so it can
  // pass a `/path?query` URL to `router.replace`.
  usePathname: () => "/analytics/costs",
  useSearchParams: () => new URLSearchParams(""),
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

import AnalyticsCostsPage from "./page";

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
  return render(<AnalyticsCostsPage />, { wrapper: Wrapper });
}

describe("AnalyticsCostsPage", () => {
  const defaultTimeseries: TenantCostTimeseriesResponse = {
    from: "2026-03-29T00:00:00Z",
    to: "2026-04-29T00:00:00Z",
    bucket: "1d",
    series: [
      { t: "2026-04-27T00:00:00Z", cost: 1.5 },
      { t: "2026-04-28T00:00:00Z", cost: 2.0 },
      { t: "2026-04-29T00:00:00Z", cost: 0.8 },
    ],
  };

  beforeEach(() => {
    getTenantBudget.mockReset();
    getDashboardCosts.mockReset();
    getDashboardAgents.mockReset();
    getAgentBudget.mockReset();
    setTenantBudget.mockReset();
    toastMock.mockReset();
    getTenantCostTimeseries.mockReset();
    // Timeseries defaults to a small resolved response so most tests
    // don't need to set it up explicitly.
    getTenantCostTimeseries.mockResolvedValue(defaultTimeseries);
  });

  it("renders tenant budget summary and the per-agent budgets grid", async () => {
    getTenantBudget.mockResolvedValue({
      dailyBudget: 50,
    } as BudgetResponse);
    getDashboardCosts.mockResolvedValue({
      totalCost: 12.5,
    } as CostDashboardSummary);
    getDashboardAgents.mockResolvedValue([
      {
        name: "ada",
        displayName: "Ada",
        role: null,
        registeredAt: new Date().toISOString(),
      } as AgentDashboardSummary,
      {
        name: "bob",
        displayName: "Bob",
        role: null,
        registeredAt: new Date().toISOString(),
      } as AgentDashboardSummary,
    ]);
    getAgentBudget.mockImplementation(async (id) =>
      id === "ada"
        ? ({ dailyBudget: 5 } as BudgetResponse)
        : Promise.reject(new Error("no budget")),
    );

    renderPage();

    // The virtualised grid container should appear once data loads.
    await waitFor(() => {
      expect(screen.getByRole("grid", { name: "Per-agent budgets" })).toBeInTheDocument();
    });
    // Tenant budget current label (rendered outside the virtualised table).
    expect(screen.getByText(/Current: \$50\.00\/day/)).toBeInTheDocument();
    // Spend to date label (rendered outside the virtualised table).
    expect(screen.getByText(/Spend to date: \$12\.50/)).toBeInTheDocument();
  });

  it("renders the cost-over-time card heading", async () => {
    getTenantBudget.mockResolvedValue({ dailyBudget: 10 } as BudgetResponse);
    getDashboardCosts.mockResolvedValue({ totalCost: 0 } as CostDashboardSummary);
    getDashboardAgents.mockResolvedValue([]);

    renderPage();

    await waitFor(() => {
      expect(screen.getByText("Cost over time")).toBeInTheDocument();
    });
  });

  it("renders the per-agent budgets grid with role=grid", async () => {
    getTenantBudget.mockResolvedValue({ dailyBudget: 10 } as BudgetResponse);
    getDashboardCosts.mockResolvedValue({ totalCost: 0 } as CostDashboardSummary);
    getDashboardAgents.mockResolvedValue([
      {
        name: "ada",
        displayName: "Ada",
        role: null,
        registeredAt: new Date().toISOString(),
      } as AgentDashboardSummary,
    ]);
    getAgentBudget.mockResolvedValue({ dailyBudget: 5 } as BudgetResponse);

    renderPage();

    await waitFor(() => {
      expect(screen.getByRole("grid", { name: "Per-agent budgets" })).toBeInTheDocument();
    });
  });

  it("renders empty state when no agents are registered", async () => {
    getTenantBudget.mockResolvedValue({
      dailyBudget: 10,
    } as BudgetResponse);
    getDashboardCosts.mockResolvedValue({
      totalCost: 0,
    } as CostDashboardSummary);
    getDashboardAgents.mockResolvedValue([]);

    renderPage();

    await waitFor(() => {
      expect(screen.getByText("No agents registered.")).toBeInTheDocument();
    });
  });
});
