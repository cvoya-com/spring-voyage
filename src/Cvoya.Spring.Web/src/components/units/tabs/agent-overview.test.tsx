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

import AgentOverviewTab from "./agent-overview";

describe("AgentOverviewTab adapter", () => {
  it("forwards the node to the unified OverviewTab as an Agent subject", () => {
    const node: AgentNode = {
      kind: "Agent",
      id: "ada",
      name: "Ada",
      status: "running",
    };
    render(<AgentOverviewTab node={node} path={[node]} />);
    const el = screen.getByTestId("unified-overview-tab");
    expect(el.dataset.kind).toBe("Agent");
    expect(el.dataset.id).toBe("ada");
  });

  it("renders nothing when invoked with a non-Agent node", () => {
    const node: UnitNode = {
      kind: "Unit",
      id: "engineering",
      name: "Engineering",
      status: "running",
    };
    const { container } = render(
      <AgentOverviewTab node={node} path={[node]} />,
    );
    expect(container.firstChild).toBeNull();
  });
});
