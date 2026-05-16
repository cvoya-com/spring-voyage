/**
 * Tests for the Agent Skills tab wrapper (#2271, #2362).
 *
 * The wrapper guards on the node kind and forwards to the canonical
 * `<EquippedSkillsTab>`. Body behaviour (equip / unequip / inherited
 * overlay) is covered by `equipped-skills-tab.test.tsx`; here we only
 * verify the wrapper boundary.
 */

import type { ReactNode } from "react";

import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { render, screen } from "@testing-library/react";
import { describe, expect, it, vi } from "vitest";

import type { AgentDetailResponse } from "@/lib/api/types";

import type { AgentNode, UnitNode } from "../aggregate";

vi.mock("@/lib/api/client", () => ({
  api: {
    getAgentSkills: vi.fn().mockResolvedValue({ skills: [] }),
    getAgent: vi.fn().mockResolvedValue({
      agent: {
        id: "ada",
        name: "ada",
        displayName: "Ada",
        description: "",
        role: null,
        registeredAt: "2026-01-01T00:00:00Z",
        enabled: true,
        executionMode: "Auto",
        parentUnit: null,
        parentUnitId: null,
        model: null,
        specialty: null,
      },
      status: null,
    } as unknown as AgentDetailResponse),
  },
}));

import AgentSkillsTab from "./agent-skills";

function Wrapper({ children }: { children: ReactNode }) {
  const client = new QueryClient({
    defaultOptions: { queries: { retry: false, gcTime: 0, staleTime: 0 } },
  });
  return <QueryClientProvider client={client}>{children}</QueryClientProvider>;
}

const agentNode: AgentNode = {
  kind: "Agent",
  id: "ada",
  name: "Ada",
  status: "running",
};

describe("AgentSkillsTab (wrapper)", () => {
  it("forwards an Agent node to <EquippedSkillsTab>", async () => {
    render(<AgentSkillsTab node={agentNode} path={[agentNode]} />, {
      wrapper: Wrapper,
    });
    expect(await screen.findByTestId("tab-agent-skills")).toBeInTheDocument();
  });

  it("renders nothing for a non-Agent node (registry-guard)", () => {
    const unitNode: UnitNode = {
      kind: "Unit",
      id: "engineering",
      name: "Engineering",
      status: "running",
    };
    const { container } = render(
      <AgentSkillsTab node={unitNode} path={[unitNode]} />,
      { wrapper: Wrapper },
    );
    expect(container.firstChild).toBeNull();
  });
});
