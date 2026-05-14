import { render, screen } from "@testing-library/react";
import { describe, expect, it, vi } from "vitest";

import type { UnitNode } from "../aggregate";

vi.mock("@/components/units/tab-impls/policies-tab", () => ({
  PoliciesTab: ({ kind, id }: { kind: string; id: string }) => (
    <div data-testid="canonical-policies-tab" data-kind={kind} data-id={id} />
  ),
}));

import UnitPoliciesTab from "./unit-policies";

describe("UnitPoliciesTab adapter (#2255)", () => {
  it("delegates to the canonical PoliciesTab with kind='Unit' and node.id", () => {
    const node: UnitNode = {
      kind: "Unit",
      id: "engineering",
      name: "Engineering",
      status: "running",
    };
    render(<UnitPoliciesTab node={node} path={[node]} />);
    const tab = screen.getByTestId("canonical-policies-tab");
    expect(tab.dataset.kind).toBe("Unit");
    expect(tab.dataset.id).toBe("engineering");
  });

  it("renders nothing for non-unit nodes", () => {
    const node = {
      kind: "Agent" as const,
      id: "ada",
      name: "Ada",
      status: "running" as const,
    };
    const { container } = render(<UnitPoliciesTab node={node} path={[node]} />);
    expect(container).toBeEmptyDOMElement();
  });
});
