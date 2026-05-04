import { fireEvent, render, screen } from "@testing-library/react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

// The Explorer pane now hosts `<UnitPaneActions>` (#980 item 3). Stub it
// out here so these scaffold tests don't have to wire a TanStack Query
// client + Next router mock — those concerns are covered by
// `unit-pane-actions.test.tsx`.
vi.mock("./unit-pane-actions", () => ({
  UnitPaneActions: () => null,
}));

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

  it("ships a search affordance", () => {
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

  describe("tabstrip keyboard navigation (V21-explorer-tabstrip-keyboard)", () => {
    // Tenant tab order (per aggregate.ts TENANT_TABS.visible):
    //   Overview, Activity, Policies, Budgets, Memory

    it("ArrowRight activates the next tab", () => {
      const onTabChange = vi.fn();
      render(<UnitExplorer tree={tree} onTabChange={onTabChange} />);
      fireEvent.keyDown(screen.getByTestId("detail-tabstrip"), {
        key: "ArrowRight",
      });
      expect(onTabChange).toHaveBeenCalledWith("tenant-acme", "Activity");
    });

    it("ArrowLeft wraps from the first tab to the last", () => {
      const onTabChange = vi.fn();
      render(<UnitExplorer tree={tree} onTabChange={onTabChange} />);
      fireEvent.keyDown(screen.getByTestId("detail-tabstrip"), {
        key: "ArrowLeft",
      });
      expect(onTabChange).toHaveBeenCalledWith("tenant-acme", "Memory");
    });

    it("ArrowRight wraps from the last tab back to the first", () => {
      const onTabChange = vi.fn();
      render(
        <UnitExplorer
          tree={tree}
          tab="Memory"
          onTabChange={onTabChange}
        />,
      );
      fireEvent.keyDown(screen.getByTestId("detail-tabstrip"), {
        key: "ArrowRight",
      });
      expect(onTabChange).toHaveBeenCalledWith("tenant-acme", "Overview");
    });

    it("Home activates the first tab", () => {
      const onTabChange = vi.fn();
      render(
        <UnitExplorer
          tree={tree}
          tab="Budgets"
          onTabChange={onTabChange}
        />,
      );
      fireEvent.keyDown(screen.getByTestId("detail-tabstrip"), {
        key: "Home",
      });
      expect(onTabChange).toHaveBeenCalledWith("tenant-acme", "Overview");
    });

    it("End activates the last tab", () => {
      const onTabChange = vi.fn();
      render(<UnitExplorer tree={tree} onTabChange={onTabChange} />);
      fireEvent.keyDown(screen.getByTestId("detail-tabstrip"), {
        key: "End",
      });
      expect(onTabChange).toHaveBeenCalledWith("tenant-acme", "Memory");
    });
  });

  describe("search filter (#1624)", () => {
    // Richer fixture so we can assert ancestor-preservation: tenant root
    // → two siblings (Engineering, Marketing) → Engineering hosts Alice
    // and a nested Backend unit holding Bob; Marketing hosts Carol.
    const filterFixture: TreeNode = {
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
            { id: "agent-alice", name: "Alice", kind: "Agent", status: "running" },
            {
              id: "unit-backend",
              name: "Backend",
              kind: "Unit",
              status: "running",
              children: [
                { id: "agent-bob", name: "Bob", kind: "Agent", status: "running" },
              ],
            },
          ],
        },
        {
          id: "unit-marketing",
          name: "Marketing",
          kind: "Unit",
          status: "running",
          children: [
            { id: "agent-carol", name: "Carol", kind: "Agent", status: "running" },
          ],
        },
      ],
    };

    it("shows every node when the query is empty", () => {
      render(<UnitExplorer tree={filterFixture} />);
      // The tree's `defaultExpanded` only opens the root, so deeper rows
      // aren't rendered until expanded — assert the top-level rows are
      // there and the no-matches affordance is absent.
      expect(screen.getByTestId("tree-row-tenant-acme")).toBeInTheDocument();
      expect(screen.getByTestId("tree-row-unit-eng")).toBeInTheDocument();
      expect(screen.getByTestId("tree-row-unit-marketing")).toBeInTheDocument();
      expect(
        screen.queryByTestId("unit-explorer-no-matches"),
      ).not.toBeInTheDocument();
    });

    it("narrows to a leaf agent and its ancestors when the query matches that agent", () => {
      render(<UnitExplorer tree={filterFixture} />);
      fireEvent.change(screen.getByTestId("unit-explorer-search"), {
        target: { value: "alice" },
      });
      // Ancestors of the match remain visible (and auto-expanded).
      expect(screen.getByTestId("tree-row-tenant-acme")).toBeInTheDocument();
      expect(screen.getByTestId("tree-row-unit-eng")).toBeInTheDocument();
      expect(screen.getByTestId("tree-row-agent-alice")).toBeInTheDocument();
      // Sibling branches that hold no match are pruned out.
      expect(screen.queryByTestId("tree-row-unit-marketing")).not.toBeInTheDocument();
      expect(screen.queryByTestId("tree-row-agent-carol")).not.toBeInTheDocument();
      expect(screen.queryByTestId("tree-row-unit-backend")).not.toBeInTheDocument();
      expect(screen.queryByTestId("tree-row-agent-bob")).not.toBeInTheDocument();
    });

    it("keeps every descendant of an ancestor whose own name matches", () => {
      render(<UnitExplorer tree={filterFixture} />);
      fireEvent.change(screen.getByTestId("unit-explorer-search"), {
        target: { value: "engineering" },
      });
      // The matching ancestor and every descendant survive — operators
      // can drill into the surviving branch without the filter hiding
      // the children that prompted the search.
      expect(screen.getByTestId("tree-row-tenant-acme")).toBeInTheDocument();
      expect(screen.getByTestId("tree-row-unit-eng")).toBeInTheDocument();
      expect(screen.getByTestId("tree-row-agent-alice")).toBeInTheDocument();
      expect(screen.getByTestId("tree-row-unit-backend")).toBeInTheDocument();
      expect(screen.getByTestId("tree-row-agent-bob")).toBeInTheDocument();
      // The Marketing sibling drops out — no descendant matches there.
      expect(screen.queryByTestId("tree-row-unit-marketing")).not.toBeInTheDocument();
    });

    it("matches case-insensitively over agent names too, surfacing the parent unit chain", () => {
      render(<UnitExplorer tree={filterFixture} />);
      fireEvent.change(screen.getByTestId("unit-explorer-search"), {
        target: { value: "BOB" },
      });
      // Bob lives under Engineering → Backend; both must auto-expand.
      expect(screen.getByTestId("tree-row-tenant-acme")).toBeInTheDocument();
      expect(screen.getByTestId("tree-row-unit-eng")).toBeInTheDocument();
      expect(screen.getByTestId("tree-row-unit-backend")).toBeInTheDocument();
      expect(screen.getByTestId("tree-row-agent-bob")).toBeInTheDocument();
      // Bob's sibling Alice does *not* survive — only ancestors of a
      // match are preserved, not siblings.
      expect(screen.queryByTestId("tree-row-agent-alice")).not.toBeInTheDocument();
      expect(screen.queryByTestId("tree-row-unit-marketing")).not.toBeInTheDocument();
    });

    it("renders the no-matches affordance when the query matches nothing", () => {
      render(<UnitExplorer tree={filterFixture} />);
      fireEvent.change(screen.getByTestId("unit-explorer-search"), {
        target: { value: "nonexistent-zzz" },
      });
      // The tree is gone; the empty-state takes its place. The detail
      // pane is unaffected — the previously-selected node still
      // renders on the right so a stray search doesn't clear context.
      expect(screen.queryByTestId("unit-tree")).not.toBeInTheDocument();
      expect(screen.getByTestId("unit-explorer-no-matches")).toBeInTheDocument();
      expect(screen.getByTestId("unit-explorer-no-matches")).toHaveTextContent(
        "No units or agents match",
      );
    });

    it("restores the full tree when the query is cleared", () => {
      render(<UnitExplorer tree={filterFixture} />);
      const input = screen.getByTestId("unit-explorer-search");
      fireEvent.change(input, { target: { value: "alice" } });
      expect(screen.queryByTestId("tree-row-unit-marketing")).not.toBeInTheDocument();
      fireEvent.change(input, { target: { value: "" } });
      expect(screen.getByTestId("tree-row-unit-marketing")).toBeInTheDocument();
      expect(
        screen.queryByTestId("unit-explorer-no-matches"),
      ).not.toBeInTheDocument();
    });
  });
});
