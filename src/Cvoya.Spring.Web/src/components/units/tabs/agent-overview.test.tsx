import { render, screen } from "@testing-library/react";
import { describe, expect, it, vi } from "vitest";

import type { AgentNode } from "../aggregate";

vi.mock("@/app/agents/[id]/lifecycle-panel", () => ({
  LifecyclePanel: ({ agentId }: { agentId: string }) => (
    <div data-testid="legacy-lifecycle" data-agent-id={agentId} />
  ),
}));

const useAgentCostMock = vi.fn();
vi.mock("@/lib/api/queries", () => ({
  useAgentCost: (id: string) => useAgentCostMock(id),
}));

import AgentOverviewTab from "./agent-overview";

describe("AgentOverviewTab", () => {
  const node: AgentNode = {
    kind: "Agent",
    id: "ada",
    name: "Ada",
    status: "running",
  };

  it("wires the lifecycle panel and a cost summary empty-state when no cost yet", () => {
    useAgentCostMock.mockReturnValueOnce({ data: null });
    render(<AgentOverviewTab node={node} path={[node]} />);
    expect(screen.getByTestId("legacy-lifecycle").dataset.agentId).toBe("ada");
    expect(screen.getByTestId("tab-agent-overview-cost-empty")).toBeInTheDocument();
  });

  it("renders totals when cost data is available", () => {
    useAgentCostMock.mockReturnValueOnce({
      data: {
        totalCost: 1.23,
        totalInputTokens: 100,
        totalOutputTokens: 50,
        recordCount: 4,
      },
    });
    render(<AgentOverviewTab node={node} path={[node]} />);
    expect(screen.getByText("100")).toBeInTheDocument();
  });
});
