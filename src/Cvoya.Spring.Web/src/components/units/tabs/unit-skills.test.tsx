import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { render, screen } from "@testing-library/react";
import { beforeEach, describe, expect, it, vi } from "vitest";
import type { ReactNode } from "react";

import type { AgentNode, UnitNode } from "../aggregate";

const getAgentSkills = vi.fn();

vi.mock("@/lib/api/client", () => ({
  api: {
    getAgentSkills: (...args: unknown[]) => getAgentSkills(...args),
    setAgentSkills: vi.fn(),
    listSkills: vi.fn(),
  },
}));

vi.mock("@/components/ui/toast", () => ({
  useToast: () => ({ toast: vi.fn() }),
}));

import UnitSkillsTab from "./unit-skills";

function Wrapper({ children }: { children: ReactNode }) {
  const qc = new QueryClient({
    defaultOptions: {
      queries: { retry: false, gcTime: 0 },
      mutations: { retry: false },
    },
  });
  return <QueryClientProvider client={qc}>{children}</QueryClientProvider>;
}

const unitNode: UnitNode = {
  kind: "Unit",
  id: "engineering",
  name: "Engineering",
  status: "running",
};

describe("UnitSkillsTab (wrapper)", () => {
  beforeEach(() => {
    getAgentSkills.mockReset();
  });

  it("renders the CLI-deeplink placeholder for a Unit node (endpoint deferred to #2276)", () => {
    render(
      <Wrapper>
        <UnitSkillsTab node={unitNode} path={[unitNode]} />
      </Wrapper>,
    );
    const placeholder = screen.getByTestId("tab-unit-skills-cli-placeholder");
    expect(placeholder).toBeInTheDocument();
    expect(placeholder).toHaveTextContent("Manage skills via the CLI");
    expect(placeholder).toHaveTextContent("spring agent skills");
    expect(placeholder).toHaveTextContent("engineering");
    // The agent-skills hook must not fire when rendering the Unit
    // placeholder — unit-keyed endpoints don't exist today and we don't
    // want to 404 against the agent endpoint with a unit id.
    expect(getAgentSkills).not.toHaveBeenCalled();
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
        <UnitSkillsTab node={agentNode} path={[agentNode]} />
      </Wrapper>,
    );
    expect(container.firstChild).toBeNull();
  });
});
