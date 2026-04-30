import { render, screen } from "@testing-library/react";
import { describe, expect, it, vi } from "vitest";

import type { TenantNode } from "../aggregate";

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

const useTenantCostMock = vi.fn();
const useTenantCostTimeseriesMock = vi.fn();
const useDashboardCostsMock = vi.fn();

vi.mock("@/lib/api/queries", () => ({
  useTenantCost: (range: unknown) => useTenantCostMock(range),
  useTenantCostTimeseries: (window: unknown, bucket: unknown) =>
    useTenantCostTimeseriesMock(window, bucket),
  useDashboardCosts: () => useDashboardCostsMock(),
}));

import TenantBudgetsTab from "./tenant-budgets";

const DEFAULT_TIMESERIES = { data: null };
const DEFAULT_DASHBOARD_COSTS = { data: null, isPending: false };

describe("TenantBudgetsTab (#902)", () => {
  const node: TenantNode = {
    kind: "Tenant",
    id: "tenant",
    name: "Tenant",
    status: "running",
  };

  it("renders the empty state when no cost data", () => {
    useTenantCostMock.mockReturnValueOnce({ data: null });
    useTenantCostTimeseriesMock.mockReturnValueOnce(DEFAULT_TIMESERIES);
    useDashboardCostsMock.mockReturnValueOnce(DEFAULT_DASHBOARD_COSTS);
    render(<TenantBudgetsTab node={node} path={[node]} />);
    expect(screen.getByTestId("tab-tenant-budgets-empty")).toBeInTheDocument();
  });

  it("renders totals when cost data is present", () => {
    useTenantCostMock.mockReturnValueOnce({
      data: { totalCost: 5.42, recordCount: 3 },
    });
    useTenantCostTimeseriesMock.mockReturnValueOnce(DEFAULT_TIMESERIES);
    useDashboardCostsMock.mockReturnValueOnce(DEFAULT_DASHBOARD_COSTS);
    render(<TenantBudgetsTab node={node} path={[node]} />);
    expect(screen.getByText(/\$5\.42/)).toBeInTheDocument();
    expect(screen.getByText(/3 records/)).toBeInTheDocument();
  });

  it("renders the 7-day sparkline when timeseries data is available", () => {
    useTenantCostMock.mockReturnValueOnce({
      data: { totalCost: 2.0, recordCount: 1 },
    });
    useTenantCostTimeseriesMock.mockReturnValueOnce({
      data: {
        series: [
          { t: "2024-01-01T00:00:00Z", cost: 1.0 },
          { t: "2024-01-02T00:00:00Z", cost: 2.0 },
        ],
      },
    });
    useDashboardCostsMock.mockReturnValueOnce(DEFAULT_DASHBOARD_COSTS);
    render(<TenantBudgetsTab node={node} path={[node]} />);
    expect(
      screen.getByTestId("tab-tenant-budgets-sparkline"),
    ).toBeInTheDocument();
  });

  it("renders the top units by spend when cost breakdown is available", () => {
    useTenantCostMock.mockReturnValueOnce({
      data: { totalCost: 10.0, recordCount: 5 },
    });
    useTenantCostTimeseriesMock.mockReturnValueOnce(DEFAULT_TIMESERIES);
    useDashboardCostsMock.mockReturnValueOnce({
      data: {
        totalCost: 10.0,
        costsBySource: [
          { source: "unit://alpha", totalCost: 6.0 },
          { source: "unit://beta", totalCost: 4.0 },
        ],
        periodStart: null,
        periodEnd: null,
      },
      isPending: false,
    });
    render(<TenantBudgetsTab node={node} path={[node]} />);
    expect(
      screen.getByTestId("tab-tenant-budgets-top-units"),
    ).toBeInTheDocument();
    expect(
      screen.getByTestId("tab-tenant-budgets-unit-alpha"),
    ).toBeInTheDocument();
    expect(
      screen.getByTestId("tab-tenant-budgets-unit-beta"),
    ).toBeInTheDocument();
  });

  it("shows the cross-links to analytics and budgets", () => {
    useTenantCostMock.mockReturnValueOnce({ data: null });
    useTenantCostTimeseriesMock.mockReturnValueOnce(DEFAULT_TIMESERIES);
    useDashboardCostsMock.mockReturnValueOnce(DEFAULT_DASHBOARD_COSTS);
    render(<TenantBudgetsTab node={node} path={[node]} />);
    expect(screen.getByTestId("tab-tenant-budgets-costs-link")).toHaveAttribute(
      "href",
      "/analytics/costs",
    );
    expect(screen.getByTestId("tab-tenant-budgets-link")).toHaveAttribute(
      "href",
      "/budgets",
    );
  });

  it("renders null for non-Tenant nodes", () => {
    useTenantCostMock.mockReturnValueOnce({ data: null });
    useTenantCostTimeseriesMock.mockReturnValueOnce(DEFAULT_TIMESERIES);
    useDashboardCostsMock.mockReturnValueOnce(DEFAULT_DASHBOARD_COSTS);
    const unitNode = {
      kind: "Unit" as const,
      id: "unit-1",
      name: "unit-1",
      status: "running" as const,
    };
    const { container } = render(
      <TenantBudgetsTab node={unitNode} path={[unitNode]} />,
    );
    expect(container.firstChild).toBeNull();
  });
});
