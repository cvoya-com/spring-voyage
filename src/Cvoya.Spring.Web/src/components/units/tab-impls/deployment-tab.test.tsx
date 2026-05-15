import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { render, screen, waitFor } from "@testing-library/react";
import { beforeEach, describe, expect, it, vi } from "vitest";
import type { ReactNode } from "react";

const getUnitDeployment = vi.fn();
const getUnit = vi.fn();

vi.mock("@/lib/api/client", () => ({
  api: {
    getUnitDeployment: (...args: unknown[]) => getUnitDeployment(...args),
    getUnit: (...args: unknown[]) => getUnit(...args),
    startUnit: vi.fn(),
    stopUnit: vi.fn(),
  },
}));

// Mock LifecyclePanel so the Agent branch only validates the dispatcher's
// wiring; the full lifecycle panel behaviour is covered by
// lifecycle-panel.test.tsx.
vi.mock("@/components/agents/tab-impls/lifecycle-panel", () => ({
  LifecyclePanel: ({ agentId }: { agentId: string }) => (
    <div data-testid="mock-lifecycle-panel" data-agent-id={agentId} />
  ),
}));

vi.mock("@/components/ui/toast", () => ({
  useToast: () => ({ toast: vi.fn() }),
}));

import { DeploymentTab } from "./deployment-tab";

function Wrapper({ children }: { children: ReactNode }) {
  const client = new QueryClient({
    defaultOptions: {
      queries: { retry: false, gcTime: 0 },
      mutations: { retry: false },
    },
  });
  return <QueryClientProvider client={client}>{children}</QueryClientProvider>;
}

describe("DeploymentTab", () => {
  beforeEach(() => {
    vi.clearAllMocks();
    getUnitDeployment.mockReset();
    getUnit.mockReset();
  });

  it("renders the LifecyclePanel for an Agent subject", () => {
    render(
      <Wrapper>
        <DeploymentTab kind="Agent" id="deploy-test-agent" />
      </Wrapper>,
    );
    expect(screen.getByTestId("tab-agent-deployment")).toBeInTheDocument();
    expect(
      screen.getByTestId("mock-lifecycle-panel").getAttribute("data-agent-id"),
    ).toBe("deploy-test-agent");
  });

  it("renders the UnitLifecyclePanel with Start/Stop for a Unit subject (#2274)", async () => {
    getUnitDeployment.mockResolvedValue({ running: true, status: "Running" });
    getUnit.mockResolvedValue({
      id: "engineering",
      name: "Engineering",
      status: "Running",
    });

    render(
      <Wrapper>
        <DeploymentTab kind="Unit" id="engineering" />
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
    // LifecyclePanel must not mount for Unit — it would 404 against the
    // agent-keyed deployment endpoints.
    expect(screen.queryByTestId("mock-lifecycle-panel")).toBeNull();
  });
});
