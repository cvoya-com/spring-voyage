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

import TenantActivityTab from "./tenant-activity";

describe("TenantActivityTab", () => {
  it("links out to the analytics routes", () => {
    const node: TenantNode = {
      kind: "Tenant",
      id: "tenant",
      name: "Tenant",
      status: "running",
    };
    render(<TenantActivityTab node={node} path={[node]} />);
    expect(
      screen
        .getByTestId("tab-tenant-activity-throughput-link")
        .getAttribute("href"),
    ).toBe("/analytics/throughput");
    expect(
      screen.getByTestId("tab-tenant-activity-waits-link").getAttribute("href"),
    ).toBe("/analytics/waits");
  });
});
