import { render, screen } from "@testing-library/react";
import { describe, expect, it, vi } from "vitest";

import type { AgentNode, UnitNode } from "../aggregate";

vi.mock("@/components/agents/agent-initiative-panel", () => ({
  AgentInitiativePanel: ({ agentId }: { agentId: string }) => (
    <div data-testid="initiative-panel-stub" data-agent-id={agentId} />
  ),
}));

import AgentPoliciesTab from "./agent-policies";

describe("AgentPoliciesTab (issue #934)", () => {
  it("mounts the initiative panel with the agent id", () => {
    const node: AgentNode = {
      kind: "Agent",
      id: "ada",
      name: "Ada",
      status: "running",
    };
    render(<AgentPoliciesTab node={node} path={[node]} />);
    expect(screen.getByTestId("tab-agent-policies")).toBeInTheDocument();
    expect(
      screen.getByTestId("initiative-panel-stub").dataset.agentId,
    ).toBe("ada");
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
