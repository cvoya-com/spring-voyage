/**
 * Tests for the Unit Deployment tab wrapper (#2273, #2274).
 *
 * The Unit branch renders a UnitLifecyclePanel with Start/Stop controls
 * backed by the unit-keyed lifecycle endpoints (#2274). The wrapper is a
 * thin kind-guard around the unified `<DeploymentTab>` so we assert the
 * Unit-flavoured Start/Stop controls land in the DOM and the
 * agent-keyed LifecyclePanel does not mount on the Unit branch.
 */

import { render, screen, waitFor } from "@testing-library/react";
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import type { ReactNode } from "react";
import { beforeEach, describe, expect, it, vi } from "vitest";

const getUnitDeployment = vi.fn();
const getUnit = vi.fn();
const startUnit = vi.fn();
const stopUnit = vi.fn();

vi.mock("@/lib/api/client", () => ({
  api: {
    getUnitDeployment: (...args: unknown[]) => getUnitDeployment(...args),
    getUnit: (...args: unknown[]) => getUnit(...args),
    startUnit: (...args: unknown[]) => startUnit(...args),
    stopUnit: (...args: unknown[]) => stopUnit(...args),
  },
}));

// The Unit branch must not mount LifecyclePanel — guard against any
// accidental regression by making the mock raise.
const lifecyclePanelMount = vi.fn();
vi.mock("@/components/agents/tab-impls/lifecycle-panel", () => ({
  LifecyclePanel: ({ agentId }: { agentId: string }) => {
    lifecyclePanelMount(agentId);
    return <div data-testid="mock-lifecycle-panel" data-agent-id={agentId} />;
  },
}));

vi.mock("@/components/ui/toast", () => ({
  useToast: () => ({ toast: vi.fn() }),
}));

import UnitDeploymentTab from "./unit-deployment";
import type { AgentNode, UnitNode } from "../aggregate";

function Wrapper({ children }: { children: ReactNode }) {
  const client = new QueryClient({
    defaultOptions: {
      queries: { retry: false, gcTime: 0 },
      mutations: { retry: false },
    },
  });
  return <QueryClientProvider client={client}>{children}</QueryClientProvider>;
}

const unitNode: UnitNode = {
  kind: "Unit",
  id: "engineering",
  name: "Engineering",
  status: "running",
};

describe("UnitDeploymentTab (wrapper)", () => {
  beforeEach(() => {
    getUnitDeployment.mockReset();
    getUnit.mockReset();
    startUnit.mockReset();
    stopUnit.mockReset();
    lifecyclePanelMount.mockReset();
  });

  it("renders the unit lifecycle panel with Start/Stop controls for a Unit node", async () => {
    getUnitDeployment.mockResolvedValue({ running: true, status: "Running" });
    getUnit.mockResolvedValue({
      id: "engineering",
      name: "Engineering",
      status: "Running",
    });

    render(
      <Wrapper>
        <UnitDeploymentTab node={unitNode} path={[unitNode]} />
      </Wrapper>,
    );

    await waitFor(() =>
      expect(screen.getByTestId("tab-unit-deployment")).toBeInTheDocument(),
    );
    expect(getUnitDeployment).toHaveBeenCalledWith("engineering");
    expect(
      screen.getByTestId("tab-unit-deployment-start"),
    ).toBeInTheDocument();
    expect(screen.getByTestId("tab-unit-deployment-stop")).toBeInTheDocument();
    // LifecyclePanel must not mount on the Unit branch — the agent-keyed
    // lifecycle endpoints would 404 against a unit id.
    expect(lifecyclePanelMount).not.toHaveBeenCalled();
  });

  it("renders nothing for a non-Unit node (registry-guard)", () => {
    const agentNode: AgentNode = {
      kind: "Agent",
      id: "ada",
      name: "Ada",
      status: "running",
    };
    const { container } = render(
      <Wrapper>
        <UnitDeploymentTab node={agentNode} path={[agentNode]} />
      </Wrapper>,
    );
    expect(container.firstChild).toBeNull();
  });
});
