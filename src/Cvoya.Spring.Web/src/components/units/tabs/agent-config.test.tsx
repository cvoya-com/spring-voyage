import { render, screen } from "@testing-library/react";
import { describe, expect, it, vi } from "vitest";

import type { AgentNode } from "../aggregate";

vi.mock("@/components/agents/tab-impls/execution-panel", () => ({
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
vi.mock("@/components/agents/agent-budget-panel", () => ({
  AgentBudgetPanel: ({ agentId }: { agentId: string }) => (
    <div data-testid="legacy-budget-panel" data-agent-id={agentId} />
  ),
}));

const useAgentMock = vi.fn();
vi.mock("@/lib/api/queries", () => ({
  useAgent: (id: string) => useAgentMock(id),
}));

import AgentConfigTab from "./agent-config";

describe("AgentConfigTab", () => {
  it("renders execution, budget, and expertise panels wired to the agent id", () => {
    useAgentMock.mockReturnValueOnce({
      data: { agent: { parentUnit: "engineering" }, status: null },
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
    expect(screen.getByTestId("legacy-budget-panel").dataset.agentId).toBe(
      "ada",
    );
    expect(screen.getByTestId("legacy-expertise").dataset.agentId).toBe("ada");
  });

  it("renders a collapsible Debug section with the raw status payload", () => {
    useAgentMock.mockReturnValueOnce({
      data: {
        agent: { parentUnit: null },
        status: { mode: "Auto", running: true },
      },
    });
    const node: AgentNode = {
      kind: "Agent",
      id: "ada",
      name: "Ada",
      status: "running",
    };
    render(<AgentConfigTab node={node} path={[node]} />);
    const section = screen.getByTestId("agent-debug-section");
    expect(section.tagName).toBe("DETAILS");
    // Default to collapsed (no `open` attribute).
    expect(section.hasAttribute("open")).toBe(false);
    expect(screen.getByTestId("agent-debug-status").textContent).toContain(
      '"mode": "Auto"',
    );
    expect(screen.getByTestId("agent-debug-status").textContent).toContain(
      '"running": true',
    );
  });

  it("renders a debug placeholder when the status payload is null", () => {
    useAgentMock.mockReturnValueOnce({
      data: { agent: { parentUnit: null }, status: null },
    });
    const node: AgentNode = {
      kind: "Agent",
      id: "ada",
      name: "Ada",
      status: "running",
    };
    render(<AgentConfigTab node={node} path={[node]} />);
    expect(screen.getByTestId("agent-debug-status").textContent).toBe(
      "(no status reported)",
    );
  });
});
