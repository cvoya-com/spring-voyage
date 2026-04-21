import { render, screen } from "@testing-library/react";
import { describe, expect, it, vi } from "vitest";

import type { AgentNode } from "../aggregate";

vi.mock("@/app/agents/[id]/execution-panel", () => ({
  AgentExecutionPanel: ({
    agentId,
    parentUnitId,
  }: {
    agentId: string;
    parentUnitId: string | null;
  }) => (
    <div
      data-testid="legacy-execution-panel"
      data-agent-id={agentId}
      data-parent-unit-id={parentUnitId ?? ""}
    />
  ),
}));
vi.mock("@/components/expertise/agent-expertise-panel", () => ({
  AgentExpertisePanel: ({ agentId }: { agentId: string }) => (
    <div data-testid="legacy-expertise" data-agent-id={agentId} />
  ),
}));

const useAgentMock = vi.fn();
vi.mock("@/lib/api/queries", () => ({
  useAgent: (id: string) => useAgentMock(id),
}));

import AgentConfigTab from "./agent-config";

describe("AgentConfigTab", () => {
  it("renders both the execution panel and expertise panel with agent id", () => {
    useAgentMock.mockReturnValueOnce({
      data: { agent: { parentUnit: "engineering" } },
    });
    const node: AgentNode = {
      kind: "Agent",
      id: "ada",
      name: "Ada",
      status: "running",
    };
    render(<AgentConfigTab node={node} path={[node]} />);
    expect(screen.getByTestId("legacy-execution-panel").dataset.agentId).toBe(
      "ada",
    );
    expect(
      screen.getByTestId("legacy-execution-panel").dataset.parentUnitId,
    ).toBe("engineering");
    expect(screen.getByTestId("legacy-expertise").dataset.agentId).toBe(
      "ada",
    );
  });
});
