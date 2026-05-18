import type { ReactNode } from "react";

import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { fireEvent, render, screen, waitFor } from "@testing-library/react";
import { beforeEach, describe, expect, it, vi } from "vitest";

import type {
  AgentDetailResponse,
  EquippedSkillEntry,
  EquippedSkillsResponse,
  PackageDetail,
  PackageSummary,
} from "@/lib/api/types";

// API mocks. Mirror the `tools-panel` test layout — narrow vi.fn shims
// per method so tests can drive each path independently. The hooks
// under `@/lib/api/queries` wrap the `api` client, so mocking the
// underlying client is enough to exercise the full TanStack stack.
const getUnitSkills = vi.fn<(id: string) => Promise<EquippedSkillsResponse>>();
const getAgentSkills = vi.fn<(id: string) => Promise<EquippedSkillsResponse>>();
const equipUnitSkill =
  vi.fn<
    (
      id: string,
      body: { packageName: string; skillName: string },
    ) => Promise<EquippedSkillsResponse>
  >();
const equipAgentSkill =
  vi.fn<
    (
      id: string,
      body: { packageName: string; skillName: string },
    ) => Promise<EquippedSkillsResponse>
  >();
const unequipUnitSkill =
  vi.fn<
    (
      id: string,
      pkg: string,
      skill: string,
    ) => Promise<EquippedSkillsResponse>
  >();
const unequipAgentSkill =
  vi.fn<
    (
      id: string,
      pkg: string,
      skill: string,
    ) => Promise<EquippedSkillsResponse>
  >();
const listPackages = vi.fn<() => Promise<PackageSummary[]>>();
const getPackage = vi.fn<(name: string) => Promise<PackageDetail | null>>();
const getAgent = vi.fn<(id: string) => Promise<AgentDetailResponse>>();

vi.mock("@/lib/api/client", () => ({
  api: {
    getUnitSkills: (id: string) => getUnitSkills(id),
    getAgentSkills: (id: string) => getAgentSkills(id),
    equipUnitSkill: (id: string, body: { packageName: string; skillName: string }) =>
      equipUnitSkill(id, body),
    equipAgentSkill: (id: string, body: { packageName: string; skillName: string }) =>
      equipAgentSkill(id, body),
    unequipUnitSkill: (id: string, pkg: string, skill: string) =>
      unequipUnitSkill(id, pkg, skill),
    unequipAgentSkill: (id: string, pkg: string, skill: string) =>
      unequipAgentSkill(id, pkg, skill),
    listPackages: () => listPackages(),
    getPackage: (name: string) => getPackage(name),
    getAgent: (id: string) => getAgent(id),
  },
}));

import { EquippedSkillsTab } from "./equipped-skills-tab";

function Wrapper({ children }: { children: ReactNode }) {
  const client = new QueryClient({
    defaultOptions: { queries: { retry: false, gcTime: 0, staleTime: 0 } },
  });
  return <QueryClientProvider client={client}>{children}</QueryClientProvider>;
}

function buildSkill(
  packageName: string,
  skillName: string,
  overrides: Partial<EquippedSkillEntry> = {},
): EquippedSkillEntry {
  return {
    packageName,
    skillName,
    promptSummary: `Body of ${packageName}/${skillName}`,
    requiredTools: [],
    ...overrides,
  };
}

function buildAgent(
  id: string,
  parentUnitId: string | null,
  parentUnit: string | null,
): AgentDetailResponse {
  return {
    agent: {
      id,
      name: id,
      displayName: "Ada",
      description: "Test agent",
      role: "reviewer",
      registeredAt: "2026-01-01T00:00:00Z",
      enabled: true,
      executionMode: "Auto",
      parentUnit,
      parentUnitId,
      model: null,
      specialty: null,
    },
    status: null,
  } as unknown as AgentDetailResponse;
}

function pkg(
  name: string,
  skillCount: number,
  overrides: Partial<PackageSummary> = {},
): PackageSummary {
  return {
    name,
    description: null,
    unitTemplateCount: 0,
    agentTemplateCount: 0,
    skillCount,
    connectorCount: 0,
    workflowCount: 0,
    ...overrides,
  } as PackageSummary;
}

function pkgDetail(
  name: string,
  skillNames: string[],
): PackageDetail {
  return {
    name,
    description: null,
    readme: null,
    version: null,
    unitTemplates: [],
    agentTemplates: [],
    skills: skillNames.map((s) => ({
      package: name,
      name: s,
      hasTools: false,
      path: `packages/${name}/skills/${s}.md`,
    })),
    connectors: [],
    workflows: [],
    connectorDeclarations: [],
    content: [],
  } as unknown as PackageDetail;
}

beforeEach(() => {
  getUnitSkills.mockReset();
  getAgentSkills.mockReset();
  equipUnitSkill.mockReset();
  equipAgentSkill.mockReset();
  unequipUnitSkill.mockReset();
  unequipAgentSkill.mockReset();
  listPackages.mockReset();
  getPackage.mockReset();
  getAgent.mockReset();
});

describe("EquippedSkillsTab (#2362)", () => {
  it("renders the empty state for a unit with no equipped skills", async () => {
    getUnitSkills.mockResolvedValue({ skills: [] });

    render(
      <EquippedSkillsTab kind="Unit" id="engineering" name="Engineering" />,
      { wrapper: Wrapper },
    );

    expect(
      await screen.findByTestId("tab-unit-skills-empty"),
    ).toBeInTheDocument();
    expect(
      screen.queryByTestId("tab-unit-skills-list"),
    ).not.toBeInTheDocument();
    expect(getUnitSkills).toHaveBeenCalledWith("engineering");
  });

  it("renders the equipped list for a unit with skills", async () => {
    getUnitSkills.mockResolvedValue({
      skills: [
        buildSkill("acme.research", "summarise"),
        buildSkill("acme.research", "extract-citations", {
          requiredTools: [
            { name: "search", description: "Search the web", optional: false },
            { name: "fetch", description: "Fetch a URL", optional: true },
          ],
        }),
      ],
    });

    render(
      <EquippedSkillsTab kind="Unit" id="engineering" name="Engineering" />,
      { wrapper: Wrapper },
    );

    await screen.findByTestId("tab-unit-skills-list");

    expect(
      screen.getByTestId("tab-unit-skills-row-acme.research-summarise"),
    ).toBeInTheDocument();
    const second = screen.getByTestId(
      "tab-unit-skills-row-acme.research-extract-citations",
    );
    expect(second.textContent).toContain("2 tools");
  });

  it("renders the inherited badge for agent rows that come from the parent unit", async () => {
    getAgent.mockResolvedValue(
      buildAgent("agent-1", "unit-eng-id", "engineering"),
    );
    getAgentSkills.mockResolvedValue({
      skills: [buildSkill("acme.research", "direct-on-agent")],
    });
    getUnitSkills.mockResolvedValue({
      skills: [buildSkill("acme.research", "from-unit")],
    });

    render(<EquippedSkillsTab kind="Agent" id="agent-1" name="Ada" />, {
      wrapper: Wrapper,
    });

    await screen.findByTestId("tab-agent-skills-list");

    const directRow = screen.getByTestId(
      "tab-agent-skills-row-acme.research-direct-on-agent",
    );
    expect(directRow.dataset.inherited).toBe("false");

    const inheritedRow = await screen.findByTestId(
      "tab-agent-skills-row-acme.research-from-unit",
    );
    expect(inheritedRow.dataset.inherited).toBe("true");
    expect(inheritedRow.className).toMatch(/opacity-60/);

    const badge = screen.getByTestId(
      "tab-agent-skills-row-acme.research-from-unit-inherited",
    );
    expect(badge.textContent).toContain("Inherited from engineering");
    const link = badge.querySelector("a");
    expect(link?.getAttribute("href")).toBe(
      "/explorer/units/unit-eng-id?tab=Config&subtab=Skills",
    );

    // Inherited rows must not surface a Remove button (operator goes
    // to the parent unit to detach).
    expect(
      screen.queryByTestId(
        "tab-agent-skills-row-acme.research-from-unit-remove",
      ),
    ).not.toBeInTheDocument();
  });

  it("dedups inherited rows when the agent equips the same skill directly (direct wins)", async () => {
    getAgent.mockResolvedValue(
      buildAgent("agent-1", "unit-eng-id", "engineering"),
    );
    const shared = buildSkill("acme.research", "summarise");
    getAgentSkills.mockResolvedValue({ skills: [shared] });
    getUnitSkills.mockResolvedValue({ skills: [shared] });

    render(<EquippedSkillsTab kind="Agent" id="agent-1" name="Ada" />, {
      wrapper: Wrapper,
    });

    const list = await screen.findByTestId("tab-agent-skills-list");
    // The row appears exactly once — the direct entry, not greyed-out.
    const rows = list.querySelectorAll('li[data-testid^="tab-agent-skills-row-"]');
    expect(rows).toHaveLength(1);
    expect((rows[0] as HTMLElement).dataset.inherited).toBe("false");
  });

  it("opens the equip dialog and equips a skill (optimistic refresh)", async () => {
    getUnitSkills.mockResolvedValue({ skills: [] });
    listPackages.mockResolvedValue([
      pkg("acme.research", 2),
      pkg("acme.no-skills", 0),
    ]);
    getPackage.mockImplementation(async (name: string) => {
      if (name === "acme.research") {
        return pkgDetail("acme.research", ["summarise", "extract"]);
      }
      return null;
    });
    equipUnitSkill.mockResolvedValue({
      skills: [buildSkill("acme.research", "summarise")],
    });

    render(
      <EquippedSkillsTab kind="Unit" id="engineering" name="Engineering" />,
      { wrapper: Wrapper },
    );

    fireEvent.click(await screen.findByTestId("tab-unit-skills-equip-open"));
    await screen.findByTestId("tab-unit-skills-equip-drawer");

    // Drawer loads available skills from installed packages — packages
    // with skillCount=0 are filtered out before fan-out.
    await screen.findByTestId("tab-unit-skills-equip-list");
    expect(getPackage).toHaveBeenCalledWith("acme.research");
    expect(getPackage).not.toHaveBeenCalledWith("acme.no-skills");

    const equipBtn = screen.getByTestId(
      "tab-unit-skills-equip-row-acme.research_summarise-equip",
    );
    fireEvent.click(equipBtn);

    await waitFor(() => {
      expect(equipUnitSkill).toHaveBeenCalledWith("engineering", {
        packageName: "acme.research",
        skillName: "summarise",
      });
    });

    // After the mutation, the drawer closes and the new row appears.
    await waitFor(() => {
      expect(
        screen.queryByTestId("tab-unit-skills-equip-drawer"),
      ).not.toBeInTheDocument();
    });
    await screen.findByTestId(
      "tab-unit-skills-row-acme.research-summarise",
    );
  });

  it("shows the Equipped pill (not Equip button) for skills the subject already equips", async () => {
    getUnitSkills.mockResolvedValue({
      skills: [buildSkill("acme.research", "summarise")],
    });
    listPackages.mockResolvedValue([pkg("acme.research", 2)]);
    getPackage.mockResolvedValue(
      pkgDetail("acme.research", ["summarise", "extract"]),
    );

    render(
      <EquippedSkillsTab kind="Unit" id="engineering" name="Engineering" />,
      { wrapper: Wrapper },
    );

    fireEvent.click(await screen.findByTestId("tab-unit-skills-equip-open"));
    await screen.findByTestId("tab-unit-skills-equip-list");

    expect(
      screen.getByTestId(
        "tab-unit-skills-equip-row-acme.research_summarise-equipped",
      ),
    ).toBeInTheDocument();
    expect(
      screen.queryByTestId(
        "tab-unit-skills-equip-row-acme.research_summarise-equip",
      ),
    ).not.toBeInTheDocument();
    // The non-equipped skill still offers the Equip button.
    expect(
      screen.getByTestId(
        "tab-unit-skills-equip-row-acme.research_extract-equip",
      ),
    ).toBeInTheDocument();
  });

  it("unequips a direct skill after operator confirmation", async () => {
    getUnitSkills.mockResolvedValue({
      skills: [buildSkill("acme.research", "summarise")],
    });
    unequipUnitSkill.mockResolvedValue({ skills: [] });

    render(
      <EquippedSkillsTab kind="Unit" id="engineering" name="Engineering" />,
      { wrapper: Wrapper },
    );

    const removeBtn = await screen.findByTestId(
      "tab-unit-skills-row-acme.research-summarise-remove",
    );
    fireEvent.click(removeBtn);

    const confirm = await screen.findByText("Unequip");
    fireEvent.click(confirm);

    await waitFor(() => {
      expect(unequipUnitSkill).toHaveBeenCalledWith(
        "engineering",
        "acme.research",
        "summarise",
      );
    });

    // List collapses to the empty state.
    await screen.findByTestId("tab-unit-skills-empty");
  });

  it("filters the drawer by package or skill name", async () => {
    getUnitSkills.mockResolvedValue({ skills: [] });
    listPackages.mockResolvedValue([
      pkg("acme.research", 1),
      pkg("acme.coding", 1),
    ]);
    getPackage.mockImplementation(async (name: string) => {
      if (name === "acme.research") return pkgDetail(name, ["summarise"]);
      if (name === "acme.coding") return pkgDetail(name, ["refactor"]);
      return null;
    });

    render(
      <EquippedSkillsTab kind="Unit" id="engineering" name="Engineering" />,
      { wrapper: Wrapper },
    );

    fireEvent.click(await screen.findByTestId("tab-unit-skills-equip-open"));
    await screen.findByTestId("tab-unit-skills-equip-list");

    // Both rows visible.
    expect(
      screen.getByTestId(
        "tab-unit-skills-equip-row-acme.research_summarise-equip",
      ),
    ).toBeInTheDocument();
    expect(
      screen.getByTestId(
        "tab-unit-skills-equip-row-acme.coding_refactor-equip",
      ),
    ).toBeInTheDocument();

    const filter = screen.getByTestId("tab-unit-skills-equip-filter");
    fireEvent.change(filter, { target: { value: "research" } });

    expect(
      screen.getByTestId(
        "tab-unit-skills-equip-row-acme.research_summarise-equip",
      ),
    ).toBeInTheDocument();
    expect(
      screen.queryByTestId(
        "tab-unit-skills-equip-row-acme.coding_refactor-equip",
      ),
    ).not.toBeInTheDocument();
  });
});
