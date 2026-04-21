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

import TenantPoliciesTab from "./tenant-policies";

describe("TenantPoliciesTab", () => {
  it("cross-links to the /policies surface", () => {
    const node: TenantNode = {
      kind: "Tenant",
      id: "tenant",
      name: "Tenant",
      status: "running",
    };
    render(<TenantPoliciesTab node={node} path={[node]} />);
    expect(
      screen.getByTestId("tab-tenant-policies-link").getAttribute("href"),
    ).toBe("/policies");
  });
});
