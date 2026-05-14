/**
 * Tests for the Unit Deployment tab wrapper (#2273).
 *
 * In v0.1 the Unit body renders a "Deploy via CLI for now" placeholder
 * because the lifecycle endpoints are strictly agent-keyed today (see
 * #2274). The wrapper still registers and honors the canonical tab
 * position; it just routes Unit subjects to a CLI deep-link.
 */

import { render, screen } from "@testing-library/react";
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import type { ReactNode } from "react";
import { beforeEach, describe, expect, it, vi } from "vitest";

import UnitDeploymentTab from "./unit-deployment";
import type { AgentNode, UnitNode } from "../aggregate";

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

function Wrapper({ children }: { children: ReactNode }) {
  const client = new QueryClient({
    defaultOptions: { queries: { retry: false, gcTime: 0 } },
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
    lifecyclePanelMount.mockReset();
  });

  it("renders the CLI-deeplink placeholder for a Unit node", () => {
    render(
      <Wrapper>
        <UnitDeploymentTab node={unitNode} path={[unitNode]} />
      </Wrapper>,
    );
    const placeholder = screen.getByTestId(
      "tab-unit-deployment-cli-placeholder",
    );
    expect(placeholder).toBeInTheDocument();
    expect(placeholder).toHaveTextContent("Deploy via the CLI");
    expect(placeholder).toHaveTextContent("engineering");
    expect(placeholder).toHaveTextContent("spring agent deploy");
    // LifecyclePanel must not mount on the Unit branch — the lifecycle
    // endpoints are agent-keyed and a unit id would 404.
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
