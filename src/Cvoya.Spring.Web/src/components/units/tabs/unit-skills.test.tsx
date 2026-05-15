/**
 * Tests for the Unit Skills tab wrapper (#2271, #2276).
 *
 * The Unit branch renders the unified `<EquippedSkillsTab>` against the
 * unit-keyed skills endpoints. We mock the api client + assert that the
 * Unit-flavoured skills hooks fire with the unit id and that the chip
 * list + Add-skill combobox render.
 */

import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { render, screen, waitFor } from "@testing-library/react";
import { beforeEach, describe, expect, it, vi } from "vitest";
import type { ReactNode } from "react";

import type { AgentNode, UnitNode } from "../aggregate";

const getUnitSkills = vi.fn();
const setUnitSkills = vi.fn();
const listSkills = vi.fn();

vi.mock("@/lib/api/client", () => ({
  api: {
    getUnitSkills: (...args: unknown[]) => getUnitSkills(...args),
    setUnitSkills: (...args: unknown[]) => setUnitSkills(...args),
    listSkills: (...args: unknown[]) => listSkills(...args),
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
    getUnitSkills.mockReset();
    setUnitSkills.mockReset();
    listSkills.mockReset();
  });

  it("delegates to EquippedSkillsTab for a Unit node and renders equipped skills", async () => {
    getUnitSkills.mockResolvedValue({ skills: ["git", "grep"] });
    listSkills.mockResolvedValue([]);

    render(
      <Wrapper>
        <UnitSkillsTab node={unitNode} path={[unitNode]} />
      </Wrapper>,
    );

    await waitFor(() =>
      expect(screen.getByTestId("tab-unit-skills")).toBeInTheDocument(),
    );
    expect(getUnitSkills).toHaveBeenCalledWith("engineering");
    expect(screen.getByText("git")).toBeInTheDocument();
    expect(screen.getByText("grep")).toBeInTheDocument();
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
    expect(getUnitSkills).not.toHaveBeenCalled();
  });
});
