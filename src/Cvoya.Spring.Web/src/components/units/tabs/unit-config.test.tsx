import { act, fireEvent, render, screen } from "@testing-library/react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

import { EXPLORER_URL_CHANGE_EVENT } from "@/lib/explorer-url";
import type { UnitNode } from "../aggregate";

// ---------------------------------------------------------------------------
// URL helpers. The tab now writes sub-tab state via window.history.replaceState
// (not router.replace) and reads it via useSyncExternalStore. Tests drive URL
// changes by calling replaceState + dispatching the Explorer URL-change event,
// which the useSyncExternalStore subscription picks up identically to
// production.
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

// Legacy panels are mocked with a visible headline string so the
// per-sub-tab render check can assert content is actually mounted
// inside the matching `<TabsContent>`.
vi.mock("@/components/units/tab-impls/boundary-tab", () => ({
  BoundaryTab: ({ unitId }: { unitId: string }) => (
    <div data-testid="legacy-boundary" data-unit-id={unitId}>
      Boundary panel
    </div>
  ),
}));
vi.mock("@/components/units/tab-impls/connector-tab", () => ({
  ConnectorTab: ({ unitId }: { unitId: string }) => (
    <div data-testid="legacy-connector" data-unit-id={unitId}>
      Connector panel
    </div>
  ),
}));
vi.mock("@/components/units/tab-impls/execution-tab", () => ({
  ExecutionTab: ({ unitId }: { unitId: string }) => (
    <div data-testid="legacy-execution" data-unit-id={unitId}>
      Execution panel
    </div>
  ),
}));
vi.mock("@/components/units/tab-impls/secrets-tab", () => ({
  SecretsTab: ({ unitId }: { unitId: string }) => (
    <div data-testid="legacy-secrets" data-unit-id={unitId}>
      Secrets panel
    </div>
  ),
}));
vi.mock("@/components/units/tab-impls/skills-tab", () => ({
  SkillsTab: ({ unitId }: { unitId: string }) => (
    <div data-testid="legacy-skills" data-unit-id={unitId}>
      Skills panel
    </div>
  ),
}));
vi.mock("@/components/expertise/unit-expertise-panel", () => ({
  UnitExpertisePanel: ({ unitId }: { unitId: string }) => (
    <div data-testid="legacy-expertise" data-unit-id={unitId}>
      Expertise panel
    </div>
  ),
}));

import UnitConfigTab from "./unit-config";

const unit: UnitNode = {
  kind: "Unit",
  id: "engineering",
  name: "Engineering",
  status: "running",
};

describe("UnitConfigTab — sub-tab layout (QUALITY-unit-config-subtabs)", () => {
  let replaceStateSpy: ReturnType<typeof vi.spyOn>;

  beforeEach(() => {
    // Reset to a clean /units URL before each test so window.location.pathname
    // matches production and replaceState calls carry the expected prefix.
    window.history.replaceState(null, "", "/units");
    replaceStateSpy = vi.spyOn(window.history, "replaceState");
  });
  afterEach(() => {
    replaceStateSpy.mockRestore();
  });

  it("renders the Config container + six sub-tabs (Expertise added by #936)", () => {
    render(<UnitConfigTab node={unit} path={[unit]} />);

    expect(screen.getByTestId("tab-unit-config")).toBeInTheDocument();
    const tabs = screen.getAllByRole("tab");
    expect(tabs).toHaveLength(6);
    expect(tabs.map((t) => t.textContent)).toEqual([
      "Boundary",
      "Execution",
      "Connector",
      "Skills",
      "Secrets",
      "Expertise",
    ]);
  });

  it("activates the Expertise sub-tab when ?subtab=Expertise is on first render", () => {
    act(() => {
      setSearchParams(new URLSearchParams("subtab=Expertise"));
    });
    render(<UnitConfigTab node={unit} path={[unit]} />);
    expect(
      screen.getByRole("tab", { name: "Expertise" }),
    ).toHaveAttribute("aria-selected", "true");
    expect(screen.getByTestId("legacy-expertise").dataset.unitId).toBe(
      "engineering",
    );
  });

  it("defaults to the first sub-tab (Boundary) when no ?subtab= is set", () => {
    render(<UnitConfigTab node={unit} path={[unit]} />);

    expect(
      screen.getByRole("tab", { name: "Boundary" }),
    ).toHaveAttribute("aria-selected", "true");
    // Only the active panel is in the DOM — the `<TabsContent>` primitive
    // unmounts inactive panels, so the Boundary mock is visible and the
    // rest aren't.
    expect(screen.getByTestId("legacy-boundary")).toBeInTheDocument();
    expect(screen.queryByTestId("legacy-execution")).not.toBeInTheDocument();
    expect(screen.queryByTestId("legacy-connector")).not.toBeInTheDocument();
    expect(screen.queryByTestId("legacy-skills")).not.toBeInTheDocument();
    expect(screen.queryByTestId("legacy-secrets")).not.toBeInTheDocument();
  });

  it("pre-selects the Boundary panel when ?subtab=Boundary is on first render", () => {
    // Pins the deep-link use-case: full Explorer URL with the Boundary
    // surface preselected (per the issue description).
    act(() => {
      setSearchParams(
        new URLSearchParams("node=engineering&tab=Config&subtab=Boundary"),
      );
    });
    render(<UnitConfigTab node={unit} path={[unit]} />);

    expect(
      screen.getByRole("tab", { name: "Boundary" }),
    ).toHaveAttribute("aria-selected", "true");
    expect(screen.getByTestId("legacy-boundary")).toBeInTheDocument();
  });

  it("pre-selects a non-default sub-tab when ?subtab= carries its value (Secrets)", () => {
    act(() => {
      setSearchParams(new URLSearchParams("subtab=Secrets"));
    });
    render(<UnitConfigTab node={unit} path={[unit]} />);

    expect(
      screen.getByRole("tab", { name: "Secrets" }),
    ).toHaveAttribute("aria-selected", "true");
    expect(screen.getByTestId("legacy-secrets")).toBeInTheDocument();
    expect(screen.queryByTestId("legacy-boundary")).not.toBeInTheDocument();
  });

  it("falls back to the default sub-tab when ?subtab= carries an unknown value", () => {
    act(() => {
      setSearchParams(new URLSearchParams("subtab=Ghost"));
    });
    render(<UnitConfigTab node={unit} path={[unit]} />);

    expect(
      screen.getByRole("tab", { name: "Boundary" }),
    ).toHaveAttribute("aria-selected", "true");
  });

  it("writes ?subtab=<name> via replaceState while preserving node + tab when a sub-tab is clicked", () => {
    act(() => {
      setSearchParams(new URLSearchParams("node=engineering&tab=Config"));
    });
    replaceStateSpy.mockClear();
    render(<UnitConfigTab node={unit} path={[unit]} />);

    fireEvent.click(screen.getByRole("tab", { name: "Secrets" }));

    // Filter to calls that write a subtab (the click handler's replaceState).
    const subtabCalls = replaceStateSpy.mock.calls.filter(
      ([, , url]) => typeof url === "string" && (url as string).includes("subtab="),
    );
    expect(subtabCalls.length).toBeGreaterThan(0);
    const url = subtabCalls.at(-1)?.[2] as string;
    expect(url).toMatch(/subtab=Secrets/);
    // node + tab round-trip so the Explorer's deep-link contract stays intact.
    expect(url).toMatch(/node=engineering/);
    expect(url).toMatch(/tab=Config/);
    // Must include the pathname, not a bare ?query string.
    expect(url).toMatch(/^\/units\?/);
  });

  it("renders each sub-tab's panel body when that sub-tab is activated", () => {
    render(<UnitConfigTab node={unit} path={[unit]} />);

    // Start at Boundary (default).
    expect(screen.getByText("Boundary panel")).toBeInTheDocument();

    fireEvent.click(screen.getByRole("tab", { name: "Execution" }));
    expect(screen.getByText("Execution panel")).toBeInTheDocument();

    fireEvent.click(screen.getByRole("tab", { name: "Connector" }));
    expect(screen.getByText("Connector panel")).toBeInTheDocument();

    fireEvent.click(screen.getByRole("tab", { name: "Skills" }));
    expect(screen.getByText("Skills panel")).toBeInTheDocument();

    fireEvent.click(screen.getByRole("tab", { name: "Secrets" }));
    expect(screen.getByText("Secrets panel")).toBeInTheDocument();
  });

  it("passes the unit id through to every legacy panel", () => {
    render(<UnitConfigTab node={unit} path={[unit]} />);
    expect(screen.getByTestId("legacy-boundary").dataset.unitId).toBe(
      "engineering",
    );

    fireEvent.click(screen.getByRole("tab", { name: "Execution" }));
    expect(screen.getByTestId("legacy-execution").dataset.unitId).toBe(
      "engineering",
    );

    fireEvent.click(screen.getByRole("tab", { name: "Connector" }));
    expect(screen.getByTestId("legacy-connector").dataset.unitId).toBe(
      "engineering",
    );

    fireEvent.click(screen.getByRole("tab", { name: "Skills" }));
    expect(screen.getByTestId("legacy-skills").dataset.unitId).toBe(
      "engineering",
    );

    fireEvent.click(screen.getByRole("tab", { name: "Secrets" }));
    expect(screen.getByTestId("legacy-secrets").dataset.unitId).toBe(
      "engineering",
    );
  });
});
