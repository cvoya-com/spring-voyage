import { render, screen } from "@testing-library/react";
import { describe, expect, it, vi } from "vitest";

import type { AgentNode, HumanNode } from "../aggregate";

vi.mock("@/components/units/tab-impls/overview-tab", () => ({
  OverviewTab: ({ kind, node }: { kind: string; node: { id: string } }) => (
    <div
      data-testid="unified-overview-tab"
      data-kind={kind}
      data-id={node.id}
    />
  ),
}));

import HumanOverviewTab from "./human-overview";

describe("HumanOverviewTab adapter (#2267)", () => {
  it("forwards the node to the unified OverviewTab as a Human subject", () => {
    const node: HumanNode = {
      kind: "Human",
      id: "11111111-1111-1111-1111-111111111111",
      name: "savas",
      status: "running",
    };
    render(<HumanOverviewTab node={node} path={[node]} />);
    const el = screen.getByTestId("unified-overview-tab");
    expect(el.dataset.kind).toBe("Human");
    expect(el.dataset.id).toBe("11111111-1111-1111-1111-111111111111");
  });

  it("renders nothing when invoked with a non-Human node — kind guard prevents tab-impl mounting", () => {
    const node: AgentNode = {
      kind: "Agent",
      id: "ada",
      name: "Ada",
      status: "running",
    };
    const { container } = render(
      <HumanOverviewTab node={node} path={[node]} />,
    );
    expect(container.firstChild).toBeNull();
  });
});
