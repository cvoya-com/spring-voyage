import { act, fireEvent, render, screen } from "@testing-library/react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

import { EXPLORER_URL_CHANGE_EVENT } from "@/lib/explorer-url";

// ---------------------------------------------------------------------------
// URL helpers — the canonical Config tab writes sub-tab state via
// window.history.replaceState + a custom event, same shape as the
// legacy unit-config used. Tests drive URL changes by calling
// replaceState and dispatching the Explorer URL-change event so the
// component's useSyncExternalStore subscription picks up the new
// snapshot exactly like production.
// ---------------------------------------------------------------------------

function setSearchParams(next: URLSearchParams) {
  const qs = next.toString();
  window.history.replaceState(
    null,
    "",
    qs ? `${window.location.pathname}?${qs}` : window.location.pathname,
  );
  window.dispatchEvent(new Event(EXPLORER_URL_CHANGE_EVENT));
}

// Mock every panel body with a visible headline. Each sub-tab body
// renders one of these — the per-sub-tab content check asserts that
// the right testid lands inside the matching `<TabsContent>` slot.

vi.mock("@/components/units/tab-impls/boundary-tab", () => ({
  BoundaryTab: ({ unitId }: { unitId: string }) => (
    <div data-testid="legacy-boundary" data-unit-id={unitId}>Boundary</div>
  ),
}));
vi.mock("@/components/units/tab-impls/connector-tab", () => ({
  ConnectorTab: ({ unitId }: { unitId: string }) => (
    <div data-testid="legacy-connector" data-unit-id={unitId}>Connector</div>
  ),
}));
vi.mock("@/components/units/tab-impls/execution-tab", () => ({
  ExecutionTab: ({ unitId }: { unitId: string }) => (
    <div data-testid="legacy-execution" data-unit-id={unitId}>Execution</div>
  ),
}));
vi.mock("@/components/units/tab-impls/secrets-tab", () => ({
  SecretsTab: ({ unitId }: { unitId: string }) => (
    <div data-testid="legacy-secrets" data-unit-id={unitId}>Secrets</div>
  ),
}));
vi.mock("@/components/units/tab-impls/tools-panel", () => ({
  ToolsPanel: ({
    kind,
    id,
    parentUnitId,
  }: {
    kind: string;
    id: string;
    parentUnitId?: string | null;
  }) => (
    <div
      data-testid="legacy-tools-panel"
      data-kind={kind}
      data-id={id}
      data-parent-unit-id={parentUnitId ?? ""}
    >
      Tools panel
    </div>
  ),
}));
vi.mock("@/components/expertise/unit-expertise-panel", () => ({
  UnitExpertisePanel: ({ unitId }: { unitId: string }) => (
    <div data-testid="legacy-unit-expertise" data-unit-id={unitId}>
      Unit expertise
    </div>
  ),
}));
vi.mock("@/components/expertise/agent-expertise-panel", () => ({
  AgentExpertisePanel: ({ agentId }: { agentId: string }) => (
    <div data-testid="legacy-agent-expertise" data-agent-id={agentId}>
      Agent expertise
    </div>
  ),
}));
vi.mock("@/components/units/tab-impls/unit-general-panel", () => ({
  UnitGeneralPanel: ({ unitId }: { unitId: string }) => (
    <div data-testid="legacy-unit-general" data-unit-id={unitId}>
      Unit general
    </div>
  ),
}));
vi.mock("@/components/units/tab-impls/agent-general-panel", () => ({
  AgentGeneralPanel: ({ agentId }: { agentId: string }) => (
    <div data-testid="legacy-agent-general" data-agent-id={agentId}>
      Agent general
    </div>
  ),
}));
vi.mock("@/components/agents/tab-impls/execution-panel", () => ({
  AgentExecutionPanel: ({
    agentId,
    parentUnitId,
  }: {
    agentId: string;
    parentUnitId: string | null;
  }) => (
    <div
      data-testid="legacy-agent-execution"
      data-agent-id={agentId}
      data-parent-unit-id={parentUnitId ?? ""}
    >
      Agent execution
    </div>
  ),
}));
vi.mock("@/components/agents/agent-budget-panel", () => ({
  AgentBudgetPanel: ({ agentId }: { agentId: string }) => (
    <div data-testid="legacy-agent-budget" data-agent-id={agentId}>
      Agent budget
    </div>
  ),
}));
vi.mock("@/components/units/unit-budget-panel", () => ({
  UnitBudgetPanel: ({ unitId }: { unitId: string }) => (
    <div data-testid="legacy-unit-budget" data-unit-id={unitId}>
      Unit budget
    </div>
  ),
}));
vi.mock("@/components/settings/agent-overrides-panel", () => ({
  AgentOverridesPanel: ({ agentId }: { agentId?: string }) => (
    <div data-testid="legacy-agent-overrides" data-agent-id={agentId ?? ""}>
      Agent overrides
    </div>
  ),
}));
vi.mock("@/components/settings/tenant-defaults-panel", () => ({
  TenantDefaultsPanel: () => (
    <div data-testid="legacy-tenant-defaults">Tenant defaults</div>
  ),
}));
vi.mock("@/components/settings/budget-panel", () => ({
  BudgetPanel: () => <div data-testid="legacy-budget-panel">Tenant budget</div>,
}));
vi.mock("@/components/settings/cloning-policy-panel", () => ({
  CloningPolicyPanel: () => (
    <div data-testid="legacy-cloning-policy">Cloning policy</div>
  ),
  CloningPolicyIcon: () => null,
}));

import { ConfigTab } from "./config-tab";

describe("ConfigTab — canonical sub-tab strip (#2254)", () => {
  let replaceStateSpy: ReturnType<typeof vi.spyOn>;

  beforeEach(() => {
    window.history.replaceState(null, "", "/units");
    replaceStateSpy = vi.spyOn(window.history, "replaceState");
  });
  afterEach(() => {
    replaceStateSpy.mockRestore();
  });

  describe("Tenant", () => {
    it("renders the three tenant sub-tabs in canonical order", () => {
      render(<ConfigTab kind="Tenant" id="tenant" />);

      expect(screen.getByTestId("tab-tenant-config")).toBeInTheDocument();
      const tabs = screen.getAllByRole("tab");
      expect(tabs.map((t) => t.textContent)).toEqual([
        "Secrets",
        "Budget",
        "Cloning",
      ]);
    });

    it("defaults to the Secrets sub-tab and renders <TenantDefaultsPanel>", () => {
      render(<ConfigTab kind="Tenant" id="tenant" />);
      expect(
        screen.getByRole("tab", { name: "Secrets" }),
      ).toHaveAttribute("aria-selected", "true");
      expect(screen.getByTestId("legacy-tenant-defaults")).toBeInTheDocument();
    });

    it("renders the embedded `/settings` panels — Budget + Cloning are the same components", () => {
      render(<ConfigTab kind="Tenant" id="tenant" />);

      fireEvent.click(screen.getByRole("tab", { name: "Budget" }));
      expect(screen.getByTestId("legacy-budget-panel")).toBeInTheDocument();

      fireEvent.click(screen.getByRole("tab", { name: "Cloning" }));
      expect(screen.getByTestId("legacy-cloning-policy")).toBeInTheDocument();
    });
  });

  describe("Unit", () => {
    it("renders nine sub-tabs — General first, then existing six plus Budget + Debug (#2337 renames Skills→Tools)", () => {
      render(<ConfigTab kind="Unit" id="engineering" />);

      expect(screen.getByTestId("tab-unit-config")).toBeInTheDocument();
      const tabs = screen.getAllByRole("tab");
      expect(tabs.map((t) => t.textContent)).toEqual([
        "General",
        "Boundary",
        "Execution",
        "Instructions",
        "Connector",
        "Tools",
        "Secrets",
        "Budget",
        "Debug",
      ]);
    });

    it("renders the Tools sub-tab via <ToolsPanel kind='Unit' …> (#2337)", () => {
      render(<ConfigTab kind="Unit" id="engineering" />);
      fireEvent.click(screen.getByRole("tab", { name: "Tools" }));
      const panel = screen.getByTestId("legacy-tools-panel");
      expect(panel.dataset.kind).toBe("Unit");
      expect(panel.dataset.id).toBe("engineering");
    });

    it("defaults to the General sub-tab", () => {
      render(<ConfigTab kind="Unit" id="engineering" />);
      expect(
        screen.getByRole("tab", { name: "General" }),
      ).toHaveAttribute("aria-selected", "true");
      expect(screen.getByTestId("legacy-unit-general").dataset.unitId).toBe(
        "engineering",
      );
    });

    it("renders the Budget sub-tab with the live unit budget editor (#2280)", () => {
      render(<ConfigTab kind="Unit" id="engineering" />);
      fireEvent.click(screen.getByRole("tab", { name: "Budget" }));
      expect(screen.getByTestId("legacy-unit-budget").dataset.unitId).toBe(
        "engineering",
      );
    });

    it("renders the Debug sub-tab with a collapsed details element", () => {
      render(<ConfigTab kind="Unit" id="engineering" />);
      fireEvent.click(screen.getByRole("tab", { name: "Debug" }));
      const section = screen.getByTestId("agent-debug-section");
      expect(section.tagName).toBe("DETAILS");
      expect(section.hasAttribute("open")).toBe(false);
    });

    it("pre-selects a non-default sub-tab when ?subtab= carries its value (Boundary)", () => {
      act(() => {
        setSearchParams(new URLSearchParams("subtab=Boundary"));
      });
      render(<ConfigTab kind="Unit" id="engineering" />);
      expect(
        screen.getByRole("tab", { name: "Boundary" }),
      ).toHaveAttribute("aria-selected", "true");
    });
  });

  describe("Agent", () => {
    it("renders eight sub-tabs — General / Execution / Instructions / Budget / Connector / Tools / Secrets / Debug (#2337 renames Skills→Tools)", () => {
      render(<ConfigTab kind="Agent" id="ada" />);

      expect(screen.getByTestId("tab-agent-config")).toBeInTheDocument();
      const tabs = screen.getAllByRole("tab");
      expect(tabs.map((t) => t.textContent)).toEqual([
        "General",
        "Execution",
        "Instructions",
        "Budget",
        "Connector",
        "Tools",
        "Secrets",
        "Debug",
      ]);
    });

    it("defaults to the General sub-tab and wires it with agentId", () => {
      render(
        <ConfigTab
          kind="Agent"
          id="ada"
          parentUnitId="engineering"
        />,
      );
      expect(
        screen.getByRole("tab", { name: "General" }),
      ).toHaveAttribute("aria-selected", "true");
      expect(screen.getByTestId("legacy-agent-general").dataset.agentId).toBe(
        "ada",
      );
    });

    it("renders the Execution sub-tab with parentUnitId", () => {
      render(
        <ConfigTab
          kind="Agent"
          id="ada"
          parentUnitId="engineering"
        />,
      );
      fireEvent.click(screen.getByRole("tab", { name: "Execution" }));
      const exec = screen.getByTestId("legacy-agent-execution");
      expect(exec.dataset.agentId).toBe("ada");
      expect(exec.dataset.parentUnitId).toBe("engineering");
    });

    it("renders the Budget sub-tab with the live agent budget editor", () => {
      render(<ConfigTab kind="Agent" id="ada" />);
      fireEvent.click(screen.getByRole("tab", { name: "Budget" }));
      expect(screen.getByTestId("legacy-agent-budget").dataset.agentId).toBe(
        "ada",
      );
    });

    it("renders the Connector sub-tab as an inherited read-only view linking to the owning unit (#2279)", () => {
      render(
        <ConfigTab
          kind="Agent"
          id="ada"
          parentUnitId="engineering"
        />,
      );
      fireEvent.click(screen.getByRole("tab", { name: "Connector" }));
      const inherited = screen.getByTestId(
        "tab-agent-config-connector-inherited",
      );
      expect(inherited).toBeInTheDocument();
      expect(
        inherited.querySelector("a")?.getAttribute("href"),
      ).toBe("?node=engineering&tab=Config&subtab=Connector");
    });

    it("renders the Tools sub-tab via <ToolsPanel kind='Agent' …> (#2337)", () => {
      render(
        <ConfigTab
          kind="Agent"
          id="ada"
          parentUnitId="engineering"
        />,
      );
      fireEvent.click(screen.getByRole("tab", { name: "Tools" }));
      const panel = screen.getByTestId("legacy-tools-panel");
      expect(panel.dataset.kind).toBe("Agent");
      expect(panel.dataset.id).toBe("ada");
      expect(panel.dataset.parentUnitId).toBe("engineering");
    });

    it("renders the Secrets sub-tab via <AgentOverridesPanel agentId=…>", () => {
      render(<ConfigTab kind="Agent" id="ada" />);
      fireEvent.click(screen.getByRole("tab", { name: "Secrets" }));
      expect(
        screen.getByTestId("legacy-agent-overrides").dataset.agentId,
      ).toBe("ada");
    });

    it("renders the Debug sub-tab with the raw status payload pretty-printed", () => {
      render(
        <ConfigTab
          kind="Agent"
          id="ada"
          status={{ mode: "Auto", running: true }}
        />,
      );
      fireEvent.click(screen.getByRole("tab", { name: "Debug" }));
      const section = screen.getByTestId("agent-debug-section");
      expect(section.tagName).toBe("DETAILS");
      expect(screen.getByTestId("agent-debug-status").textContent).toContain(
        '"mode": "Auto"',
      );
    });

    it("falls back to (no status reported) when status is null", () => {
      render(<ConfigTab kind="Agent" id="ada" status={null} />);
      fireEvent.click(screen.getByRole("tab", { name: "Debug" }));
      expect(screen.getByTestId("agent-debug-status").textContent).toBe(
        "(no status reported)",
      );
    });
  });

  describe("URL contract", () => {
    it("writes ?subtab=<name> via replaceState while preserving node + tab when a sub-tab is clicked", () => {
      act(() => {
        setSearchParams(new URLSearchParams("node=ada&tab=Config"));
      });
      replaceStateSpy.mockClear();
      render(<ConfigTab kind="Agent" id="ada" />);

      fireEvent.click(screen.getByRole("tab", { name: "Budget" }));

      const subtabCalls = replaceStateSpy.mock.calls.filter(
        ([, , url]) =>
          typeof url === "string" && (url as string).includes("subtab="),
      );
      expect(subtabCalls.length).toBeGreaterThan(0);
      const url = subtabCalls.at(-1)?.[2] as string;
      expect(url).toMatch(/subtab=Budget/);
      expect(url).toMatch(/node=ada/);
      expect(url).toMatch(/tab=Config/);
      expect(url).toMatch(/^\/units\?/);
    });

    it("falls back to the first sub-tab when ?subtab= carries an unknown value", () => {
      act(() => {
        setSearchParams(new URLSearchParams("subtab=Ghost"));
      });
      render(<ConfigTab kind="Tenant" id="tenant" />);
      expect(
        screen.getByRole("tab", { name: "Secrets" }),
      ).toHaveAttribute("aria-selected", "true");
    });
  });
});
