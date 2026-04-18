import { render, screen, waitFor } from "@testing-library/react";
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { beforeEach, describe, expect, it, vi } from "vitest";
import type { ReactNode } from "react";

import type {
  AgentDashboardSummary,
  BudgetResponse,
  CostDashboardSummary,
} from "@/lib/api/types";

const getTenantBudget = vi.fn<() => Promise<BudgetResponse>>();
const getDashboardCosts = vi.fn<() => Promise<CostDashboardSummary>>();
const getDashboardAgents = vi.fn<() => Promise<AgentDashboardSummary[]>>();
const getAgentBudget = vi.fn<(id: string) => Promise<BudgetResponse>>();
const setTenantBudget = vi.fn();

vi.mock("@/lib/api/client", () => ({
  api: {
    getTenantBudget: () => getTenantBudget(),
    getDashboardCosts: () => getDashboardCosts(),
    getDashboardAgents: () => getDashboardAgents(),
    getAgentBudget: (id: string) => getAgentBudget(id),
    setTenantBudget: (...args: unknown[]) => setTenantBudget(...args),
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
  beforeEach(() => {
    getTenantBudget.mockReset();
    getDashboardCosts.mockReset();
    getDashboardAgents.mockReset();
    getAgentBudget.mockReset();
    setTenantBudget.mockReset();
    toastMock.mockReset();
  });

  it("renders tenant budget, costs, and per-agent budget rows", async () => {
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

    await waitFor(() => {
      expect(screen.getByText("Ada")).toBeInTheDocument();
    });
    expect(screen.getByText("Bob")).toBeInTheDocument();
    // Ada has $5/day; Bob has none.
    expect(screen.getByText(/\$5\.00\/day/)).toBeInTheDocument();
    expect(screen.getByText(/Not set/)).toBeInTheDocument();
    // Tenant budget current label.
    expect(screen.getByText(/Current: \$50\.00\/day/)).toBeInTheDocument();
    // Spend to date label.
    expect(screen.getByText(/Spend to date: \$12\.50/)).toBeInTheDocument();
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
