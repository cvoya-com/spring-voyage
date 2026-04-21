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

import TenantOverviewTab from "./tenant-overview";

describe("TenantOverviewTab", () => {
  it("renders the empty state when tenant has no units", () => {
    const node: TenantNode = {
      kind: "Tenant",
      id: "tenant",
      name: "Tenant",
      status: "running",
      children: [],
    };
    render(<TenantOverviewTab node={node} path={[node]} />);
    expect(screen.getByTestId("tab-tenant-overview-empty")).toBeInTheDocument();
  });

  it("renders a UnitCard for each top-level unit", () => {
    const node: TenantNode = {
      kind: "Tenant",
      id: "tenant",
      name: "Tenant",
      status: "running",
      children: [
        {
          kind: "Unit",
          id: "engineering",
          name: "Engineering",
          status: "running",
        },
        {
          kind: "Unit",
          id: "platform",
          name: "Platform",
          status: "paused",
        },
      ],
    };
    render(<TenantOverviewTab node={node} path={[node]} />);
    expect(screen.getByTestId("unit-card-engineering")).toBeInTheDocument();
    expect(screen.getByTestId("unit-card-platform")).toBeInTheDocument();
  });
});
