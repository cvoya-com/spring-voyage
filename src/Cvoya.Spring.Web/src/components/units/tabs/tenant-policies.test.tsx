import { render, screen } from "@testing-library/react";
import { describe, expect, it, vi } from "vitest";

import type { TenantNode } from "../aggregate";

vi.mock("@/components/units/tab-impls/policies-tab", () => ({
  PoliciesTab: ({ kind, id }: { kind: string; id: string }) => (
    <div data-testid="canonical-policies-tab" data-kind={kind} data-id={id} />
  ),
}));

import TenantPoliciesTab from "./tenant-policies";

describe("TenantPoliciesTab adapter (#2255)", () => {
  it("delegates to the canonical PoliciesTab with kind='Tenant' and node.id", () => {
    const node: TenantNode = {
      kind: "Tenant",
      id: "tenant",
      name: "Tenant",
      status: "running",
    };
    render(<TenantPoliciesTab node={node} path={[node]} />);
    const tab = screen.getByTestId("canonical-policies-tab");
    expect(tab.dataset.kind).toBe("Tenant");
    expect(tab.dataset.id).toBe("tenant");
  });
});
