import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { render, screen, waitFor } from "@testing-library/react";
import { beforeEach, describe, expect, it, vi } from "vitest";
import type { ReactNode } from "react";

import type { AgentNode, UnitNode } from "../aggregate";

const getAgentSkills = vi.fn();
const setAgentSkills = vi.fn();
const listSkills = vi.fn();

vi.mock("@/lib/api/client", () => ({
  api: {
    getAgentSkills: (...args: unknown[]) => getAgentSkills(...args),
    setAgentSkills: (...args: unknown[]) => setAgentSkills(...args),
    listSkills: (...args: unknown[]) => listSkills(...args),
  },
}));

vi.mock("@/components/ui/toast", () => ({
  useToast: () => ({ toast: vi.fn() }),
}));

import AgentSkillsTab from "./agent-skills";

function Wrapper({ children }: { children: ReactNode }) {
  const qc = new QueryClient({
    defaultOptions: {
      queries: { retry: false, gcTime: 0 },
      mutations: { retry: false },
    },
  });
  return <QueryClientProvider client={qc}>{children}</QueryClientProvider>;
}

const agentNode: AgentNode = {
  kind: "Agent",
  id: "ada",
  name: "Ada",
  status: "running",
};

describe("AgentSkillsTab (wrapper)", () => {
  beforeEach(() => {
    getAgentSkills.mockReset();
    setAgentSkills.mockReset();
    listSkills.mockReset();
  });

  it("delegates to EquippedSkillsTab for an Agent node and renders equipped skills", async () => {
    getAgentSkills.mockResolvedValue({ skills: ["git", "grep"] });
    listSkills.mockResolvedValue([]);

    render(
      <Wrapper>
        <AgentSkillsTab node={agentNode} path={[agentNode]} />
      </Wrapper>,
    );

    await waitFor(() =>
      expect(screen.getByTestId("tab-agent-skills")).toBeInTheDocument(),
    );
    expect(getAgentSkills).toHaveBeenCalledWith("ada");
    expect(screen.getByText("git")).toBeInTheDocument();
    expect(screen.getByText("grep")).toBeInTheDocument();
  });

  it("renders nothing for a non-Agent node (registry-guard)", () => {
    const unitNode: UnitNode = {
      kind: "Unit",
      id: "engineering",
      name: "Engineering",
      status: "running",
    };
    const { container } = render(
      <Wrapper>
        <AgentSkillsTab node={unitNode} path={[unitNode]} />
      </Wrapper>,
    );
    expect(container.firstChild).toBeNull();
    expect(getAgentSkills).not.toHaveBeenCalled();
  });
});
