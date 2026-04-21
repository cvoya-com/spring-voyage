import { fireEvent, render, screen } from "@testing-library/react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

import type { TreeNode } from "./aggregate";
import {
  __resetTabRegistryForTesting,
  registerTab,
} from "./tabs";
import { UnitExplorer } from "./unit-explorer";

const tree: TreeNode = {
  id: "tenant-acme",
  name: "Acme",
  kind: "Tenant",
  status: "running",
  children: [
    {
      id: "unit-eng",
      name: "Engineering",
      kind: "Unit",
      status: "running",
      children: [
        { id: "agent-ada", name: "Ada", kind: "Agent", status: "running" },
      ],
    },
  ],
};

describe("UnitExplorer (foundation scaffold)", () => {
  beforeEach(() => __resetTabRegistryForTesting());
  afterEach(() => __resetTabRegistryForTesting());

  it("renders a tree column and a detail pane", () => {
    render(<UnitExplorer tree={tree} />);
    expect(screen.getByTestId("unit-explorer")).toBeInTheDocument();
    expect(screen.getByTestId("unit-tree")).toBeInTheDocument();
    expect(screen.getByTestId("unit-detail-pane")).toBeInTheDocument();
  });

  it("ships a search affordance even though filtering is wired by EXP-search", () => {
    render(<UnitExplorer tree={tree} />);
    expect(
      screen.getByTestId("unit-explorer-search"),
    ).toBeInTheDocument();
  });

  it("renders the kind's tab strip", () => {
    render(<UnitExplorer tree={tree} />);
    expect(screen.getByTestId("detail-tabstrip")).toBeInTheDocument();
    // Tenant gets 5 tabs by default.
    expect(screen.getAllByRole("tab")).toHaveLength(5);
  });

  it("falls back to the placeholder when no tab content is registered", () => {
    render(<UnitExplorer tree={tree} />);
    expect(screen.getByTestId("tab-placeholder-overview")).toBeInTheDocument();
  });

  it("renders the registered component when a tab is registered", () => {
    function TenantOverview() {
      return <p data-testid="registered-overview">tenant overview</p>;
    }
    registerTab("Tenant", "Overview", TenantOverview);
    render(<UnitExplorer tree={tree} />);
    expect(screen.getByTestId("registered-overview")).toBeInTheDocument();
    expect(
      screen.queryByTestId("tab-placeholder-overview"),
    ).not.toBeInTheDocument();
  });

  it("changes selection when a tree row is clicked, swapping the tab catalog to match", () => {
    render(<UnitExplorer tree={tree} />);
    fireEvent.click(screen.getByTestId("tree-row-unit-eng"));
    // Engineering is a Unit → 8 tabs (incl. Agents, Orchestration).
    expect(screen.getAllByRole("tab")).toHaveLength(8);
    expect(screen.getByTestId("detail-tab-agents")).toBeInTheDocument();
  });

  it("changes the active tab when a tab button is clicked", () => {
    render(<UnitExplorer tree={tree} />);
    fireEvent.click(screen.getByTestId("detail-tab-activity"));
    expect(screen.getByTestId("detail-tab-activity")).toHaveAttribute(
      "aria-selected",
      "true",
    );
    expect(screen.getByTestId("tab-placeholder-activity")).toBeInTheDocument();
  });

  it("wires the selected tab to its panel via aria-controls / aria-labelledby", () => {
    render(<UnitExplorer tree={tree} />);
    const selectedTab = screen.getByTestId("detail-tab-overview");
    expect(selectedTab).toHaveAttribute("aria-selected", "true");
    expect(selectedTab).toHaveAttribute("tabindex", "0");
    const panelId = selectedTab.getAttribute("aria-controls");
    expect(panelId).toBeTruthy();
    const panel = document.getElementById(panelId!);
    expect(panel).not.toBeNull();
    expect(panel).toHaveAttribute("role", "tabpanel");
    expect(panel).toHaveAttribute("aria-labelledby", selectedTab.id);
    // Inactive tabs are removed from the tab order (roving tabIndex prep).
    expect(screen.getByTestId("detail-tab-activity")).toHaveAttribute(
      "tabindex",
      "-1",
    );
  });

  it("falls back to the tree root when `selectedId` no longer maps to any node", () => {
    // URL-stale node case: the operator bookmarked or pasted a link to a
    // node that has since been deleted. The Explorer must keep working by
    // rendering the tenant root rather than throwing (or bubbling to an
    // error boundary).
    render(<UnitExplorer tree={tree} selectedId="ghost-id" />);
    expect(screen.getByTestId("unit-explorer")).toBeInTheDocument();
    // Tenant root's breadcrumb button is present and `aria-current` — the
    // detail pane resolved to the root, not to the stale id.
    expect(screen.getByTestId("detail-crumb-tenant-acme")).toHaveAttribute(
      "aria-current",
      "page",
    );
    // Tenant catalog is 5 tabs; Unit would be 8. Confirms the kind-specific
    // catalog is driven by the fallback node, not the stale id.
    expect(screen.getAllByRole("tab")).toHaveLength(5);
  });

  it("auto-snaps to the kind's first tab when the controlled `tab` is out of catalog", () => {
    // Stale URL case: `?tab=Skills` is valid for Agent but not for Tenant.
    // `<DetailPane>`'s useEffect should snap to `tabsFor("Tenant")[0]` —
    // "Overview" — and dispatch onTabChange so the URL gets corrected.
    const onTabChange = vi.fn();
    // `Skills` isn't a TabName for Tenant, so we cast through unknown to
    // stand in for a stale URL fragment the router would pass through.
    render(
      <UnitExplorer
        tree={tree}
        tab={"Skills" as unknown as never}
        onTabChange={onTabChange}
      />,
    );
    expect(onTabChange).toHaveBeenCalled();
    // Second argument is the snapped-to tab; first is the selected id.
    const call = onTabChange.mock.calls[0];
    expect(call[0]).toBe("tenant-acme");
    expect(call[1]).toBe("Overview");
  });

  it("remembers the per-node tab choice when the operator revisits a node", () => {
    // Flow: Tenant → open Activity → click Engineering → click Tenant crumb.
    // On return, Tenant's active tab should still be Activity, not snap
    // back to Overview. Pins the `tabByNode` memory.
    render(<UnitExplorer tree={tree} />);

    // On the tenant root: switch to Activity.
    fireEvent.click(screen.getByTestId("detail-tab-activity"));
    expect(screen.getByTestId("detail-tab-activity")).toHaveAttribute(
      "aria-selected",
      "true",
    );

    // Navigate to Engineering (a Unit → 8-tab catalog). First tab
    // (Overview) should be active because Engineering has no remembered
    // choice.
    fireEvent.click(screen.getByTestId("tree-row-unit-eng"));
    expect(screen.getAllByRole("tab")).toHaveLength(8);
    expect(screen.getByTestId("detail-tab-overview")).toHaveAttribute(
      "aria-selected",
      "true",
    );

    // Navigate back to the Tenant via the breadcrumb. The remembered
    // Activity tab should come back — not Overview.
    fireEvent.click(screen.getByTestId("detail-crumb-tenant-acme"));
    expect(screen.getAllByRole("tab")).toHaveLength(5);
    expect(screen.getByTestId("detail-tab-activity")).toHaveAttribute(
      "aria-selected",
      "true",
    );
  });

  it("dispatches `onTabChange` with both the selected node id and the new tab name", () => {
    // Pins the two-argument callback signature so downstream route wiring
    // (and any future refactor) can't silently drop `selectedId`.
    const onTabChange = vi.fn();
    render(<UnitExplorer tree={tree} onTabChange={onTabChange} />);

    fireEvent.click(screen.getByTestId("detail-tab-activity"));
    expect(onTabChange).toHaveBeenCalledWith("tenant-acme", "Activity");
  });
});
