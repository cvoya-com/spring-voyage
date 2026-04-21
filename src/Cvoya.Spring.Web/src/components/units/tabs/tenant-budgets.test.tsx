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
vi.mock("@/lib/api/queries", () => ({
  useTenantCost: (range: unknown) => useTenantCostMock(range),
}));

import TenantBudgetsTab from "./tenant-budgets";

describe("TenantBudgetsTab", () => {
  const node: TenantNode = {
    kind: "Tenant",
    id: "tenant",
    name: "Tenant",
    status: "running",
  };

  it("renders the empty state when no cost data", () => {
    useTenantCostMock.mockReturnValueOnce({ data: null });
    render(<TenantBudgetsTab node={node} path={[node]} />);
    expect(screen.getByTestId("tab-tenant-budgets-empty")).toBeInTheDocument();
  });

  it("renders totals when cost data is present", () => {
    useTenantCostMock.mockReturnValueOnce({
      data: { totalCost: 5.42, recordCount: 3 },
    });
    render(<TenantBudgetsTab node={node} path={[node]} />);
    expect(screen.getByText(/\$5\.42/)).toBeInTheDocument();
  });
});
