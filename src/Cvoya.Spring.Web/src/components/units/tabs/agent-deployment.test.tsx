/**
 * Tests for the Agent Deployment tab (#1119).
 *
 * Verifies that:
 *   - The tab renders and delegates to LifecyclePanel.
 *   - The correct agentId is forwarded so the lifecycle hooks fire against
 *     the right agent.
 *   - Null-guard: non-Agent nodes return null without erroring.
 */

import { render, screen } from "@testing-library/react";
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import type { ReactNode } from "react";
import { beforeEach, describe, expect, it, vi } from "vitest";

import AgentDeploymentTab from "./agent-deployment";
import type { AgentNode, UnitNode } from "../aggregate";

// Mock the LifecyclePanel so this test only validates the tab wrapper's
// wiring, not the full lifecycle panel behaviour (which has its own suite
// in lifecycle-panel.test.tsx).
vi.mock("@/components/agents/tab-impls/lifecycle-panel", () => ({
  LifecyclePanel: ({ agentId }: { agentId: string }) => (
    <div data-testid="mock-lifecycle-panel" data-agent-id={agentId} />
  ),
}));

// Silence the toast surface — not exercised in this suite.
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

describe("AgentDeploymentTab", () => {
  beforeEach(() => {
    vi.clearAllMocks();
  });

  it("renders the Deployment tab wrapper for an agent node", () => {
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

  it("renders nothing for a non-Agent node", () => {
    const { container } = render(
      <Wrapper>
        <AgentDeploymentTab node={unitNode} path={[unitNode]} />
      </Wrapper>,
    );
    expect(container.firstChild).toBeNull();
  });
});
