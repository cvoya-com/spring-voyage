import { render, screen } from "@testing-library/react";
import { describe, expect, it, vi } from "vitest";

import type { AgentNode, UnitNode } from "../aggregate";

vi.mock("@/components/units/tab-impls/overview-tab", () => ({
  OverviewTab: ({ kind, node }: { kind: string; node: { id: string } }) => (
    <div
      data-testid="unified-overview-tab"
      data-kind={kind}
      data-id={node.id}
    />
  ),
}));

import UnitOverviewTab from "./unit-overview";

describe("UnitOverviewTab adapter", () => {
  it("forwards the node to the unified OverviewTab as a Unit subject", () => {
    const node: UnitNode = {
      kind: "Unit",
      id: "engineering",
      name: "Engineering",
      status: "running",
    };
    render(<UnitOverviewTab node={node} path={[node]} />);
    const el = screen.getByTestId("unified-overview-tab");
    expect(el.dataset.kind).toBe("Unit");
    expect(el.dataset.id).toBe("engineering");
  });

  it("renders nothing when invoked with a non-Unit node", () => {
    const node: AgentNode = {
      kind: "Agent",
      id: "ada",
      name: "Ada",
      status: "running",
    };
    const { container } = render(
      <UnitOverviewTab node={node} path={[node]} />,
    );
    expect(container.firstChild).toBeNull();
  });
});
