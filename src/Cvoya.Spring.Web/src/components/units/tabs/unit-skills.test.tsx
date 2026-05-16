/**
 * Tests for the Unit Skills tab wrapper (#2271, #2362).
 *
 * The wrapper guards on the node kind and forwards to the canonical
 * `<EquippedSkillsTab>`. Body behaviour (equip / unequip / inherited)
 * is covered by `equipped-skills-tab.test.tsx`; here we only verify
 * the wrapper boundary.
 */

import type { ReactNode } from "react";

import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { render, screen } from "@testing-library/react";
import { describe, expect, it, vi } from "vitest";

import type { AgentNode, UnitNode } from "../aggregate";

vi.mock("@/lib/api/client", () => ({
  api: {
    getUnitSkills: vi.fn().mockResolvedValue({ skills: [] }),
  },
}));

import UnitSkillsTab from "./unit-skills";

function Wrapper({ children }: { children: ReactNode }) {
  const client = new QueryClient({
    defaultOptions: { queries: { retry: false, gcTime: 0, staleTime: 0 } },
  });
  return <QueryClientProvider client={client}>{children}</QueryClientProvider>;
}

const unitNode: UnitNode = {
  kind: "Unit",
  id: "engineering",
  name: "Engineering",
  status: "running",
};

describe("UnitSkillsTab (wrapper)", () => {
  it("forwards a Unit node to <EquippedSkillsTab>", async () => {
    render(<UnitSkillsTab node={unitNode} path={[unitNode]} />, {
      wrapper: Wrapper,
    });
    // The shared body assigns this testid root for the Unit variant.
    expect(await screen.findByTestId("tab-unit-skills")).toBeInTheDocument();
  });

  it("renders nothing for a non-Unit node (registry-guard)", () => {
    const agentNode: AgentNode = {
      kind: "Agent",
      id: "ada",
      name: "Ada",
      status: "running",
    };
    const { container } = render(
      <UnitSkillsTab node={agentNode} path={[agentNode]} />,
      { wrapper: Wrapper },
    );
    expect(container.firstChild).toBeNull();
  });
});
