/**
 * Tests for the Agent Deployment tab wrapper (#1119, #2273).
 *
 * Verifies that the wrapper:
 *   - Delegates to the unified `<DeploymentTab>` for an Agent node.
 *   - Forwards the agent id so the lifecycle hooks fire against the right agent.
 *   - Returns null for non-Agent nodes (registry-guard).
 */

import { render, screen } from "@testing-library/react";
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import type { ReactNode } from "react";
import { beforeEach, describe, expect, it, vi } from "vitest";

import AgentDeploymentTab from "./agent-deployment";
import type { AgentNode, UnitNode } from "../aggregate";

// Mock LifecyclePanel so this test only validates the wrapper + dispatcher
// wiring, not the full lifecycle panel behaviour (which has its own suite
// in lifecycle-panel.test.tsx).
vi.mock("@/components/agents/tab-impls/lifecycle-panel", () => ({
  LifecyclePanel: ({ agentId }: { agentId: string }) => (
    <div data-testid="mock-lifecycle-panel" data-agent-id={agentId} />
  ),
}));

vi.mock("@/components/ui/toast", () => ({
  useToast: () => ({ toast: vi.fn() }),
}));

function Wrapper({ children }: { children: ReactNode }) {
  const client = new QueryClient({
    defaultOptions: { queries: { retry: false, gcTime: 0 } },
  });
  return <QueryClientProvider client={client}>{children}</QueryClientProvider>;
}

const agentNode: AgentNode = {
  kind: "Agent",
  id: "deploy-test-agent",
  name: "deploy-test-agent",
  status: "running",
};

const unitNode: UnitNode = {
  kind: "Unit",
  id: "unit-1",
  name: "unit-1",
  status: "running",
};

describe("AgentDeploymentTab (wrapper)", () => {
  beforeEach(() => {
    vi.clearAllMocks();
  });

  it("delegates to DeploymentTab for an Agent node", () => {
    render(
      <Wrapper>
        <AgentDeploymentTab node={agentNode} path={[agentNode]} />
      </Wrapper>,
    );
    expect(screen.getByTestId("tab-agent-deployment")).toBeInTheDocument();
  });

  it("forwards the agent id to LifecyclePanel", () => {
    render(
      <Wrapper>
        <AgentDeploymentTab node={agentNode} path={[agentNode]} />
      </Wrapper>,
    );
    const panel = screen.getByTestId("mock-lifecycle-panel");
    expect(panel.getAttribute("data-agent-id")).toBe("deploy-test-agent");
  });

  it("renders nothing for a non-Agent node (registry-guard)", () => {
    const { container } = render(
      <Wrapper>
        <AgentDeploymentTab node={unitNode} path={[unitNode]} />
      </Wrapper>,
    );
    expect(container.firstChild).toBeNull();
  });
});
