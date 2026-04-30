import { render, screen } from "@testing-library/react";
import { describe, expect, it, vi } from "vitest";
import type { ReactNode } from "react";

import type { AgentNode } from "../aggregate";

vi.mock("next/link", () => ({
  default: ({
    href,
    children,
    ...rest
  }: { href: string; children: ReactNode } & Record<string, unknown>) => (
    <a href={href} {...rest}>
      {children}
    </a>
  ),
}));

vi.mock("@/components/agents/tab-impls/lifecycle-panel", () => ({
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

  it("renders the cross-portal engagement link with the agent id (E2.3 #1415)", () => {
    useAgentCostMock.mockReturnValueOnce({ data: null });
    render(<AgentOverviewTab node={node} path={[node]} />);
    const link = screen.getByTestId("agent-overview-engagement-link");
    expect(link).toHaveAttribute(
      "href",
      "/engagement/mine?agent=ada",
    );
    expect(link).toHaveTextContent("View engagements for this agent");
  });
});
