import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { render, screen } from "@testing-library/react";
import { beforeEach, describe, expect, it, vi } from "vitest";
import type { ReactNode } from "react";

import { DeploymentTab } from "./deployment-tab";

// Mock LifecyclePanel so this suite only validates the dispatcher's
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

function Wrapper({ children }: { children: ReactNode }) {
  const client = new QueryClient({
    defaultOptions: { queries: { retry: false, gcTime: 0 } },
  });
  return <QueryClientProvider client={client}>{children}</QueryClientProvider>;
}

describe("DeploymentTab", () => {
  beforeEach(() => {
    vi.clearAllMocks();
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

  it("renders the CLI-deeplink placeholder for a Unit subject (endpoint deferred to #2274)", () => {
    render(
      <Wrapper>
        <DeploymentTab kind="Unit" id="engineering" />
      </Wrapper>,
    );
    const placeholder = screen.getByTestId(
      "tab-unit-deployment-cli-placeholder",
    );
    expect(placeholder).toBeInTheDocument();
    expect(placeholder).toHaveTextContent("Deploy via the CLI");
    expect(placeholder).toHaveTextContent("engineering");
    expect(placeholder).toHaveTextContent("spring agent deploy");
    // LifecyclePanel must not mount for Unit — it would 404 against
    // the agent-keyed deployment endpoints.
    expect(screen.queryByTestId("mock-lifecycle-panel")).toBeNull();
  });
});
