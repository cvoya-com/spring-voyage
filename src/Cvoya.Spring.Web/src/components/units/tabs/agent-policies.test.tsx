import { render, screen } from "@testing-library/react";
import { describe, expect, it, vi } from "vitest";

import type { AgentNode, UnitNode } from "../aggregate";

vi.mock("@/components/units/tab-impls/policies-tab", () => ({
  PoliciesTab: ({ kind, id }: { kind: string; id: string }) => (
    <div data-testid="canonical-policies-tab" data-kind={kind} data-id={id} />
  ),
}));

import AgentPoliciesTab from "./agent-policies";

describe("AgentPoliciesTab adapter (#2255, was #934 + #534)", () => {
  it("delegates to the canonical PoliciesTab with kind='Agent' and node.id", () => {
    const node: AgentNode = {
      kind: "Agent",
      id: "ada",
      name: "Ada",
      status: "running",
    };
    render(<AgentPoliciesTab node={node} path={[node]} />);
    const tab = screen.getByTestId("canonical-policies-tab");
    expect(tab.dataset.kind).toBe("Agent");
    expect(tab.dataset.id).toBe("ada");
  });

  it("renders nothing for non-agent nodes", () => {
    const node: UnitNode = {
      kind: "Unit",
      id: "engineering",
      name: "Engineering",
      status: "running",
    };
    const { container } = render(
      <AgentPoliciesTab node={node} path={[node]} />,
    );
    expect(container).toBeEmptyDOMElement();
  });
});
