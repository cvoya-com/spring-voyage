import { fireEvent, render, screen } from "@testing-library/react";
import { describe, expect, it, vi } from "vitest";

import type { TreeNode } from "./aggregate";
import { UnitTree } from "./unit-tree";

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
        { id: "agent-grace", name: "Grace", kind: "Agent", status: "error" },
      ],
    },
    {
      id: "unit-research",
      name: "Research",
      kind: "Unit",
      status: "running",
    },
  ],
};

describe("UnitTree", () => {
  it("renders the root node with role=tree and treeitem children", () => {
    render(
      <UnitTree
        tree={tree}
        selectedId="tenant-acme"
        onSelect={vi.fn()}
        defaultExpanded={{ "tenant-acme": true }}
      />,
    );

    const treeRoot = screen.getByTestId("unit-tree");
    expect(treeRoot).toHaveAttribute("role", "tree");
    expect(treeRoot).toHaveAttribute("aria-label", "Units & agents");

    expect(screen.getByTestId("tree-row-tenant-acme")).toHaveAttribute(
      "role",
      "treeitem",
    );
  });

  it("marks the selected node with aria-selected=true", () => {
    render(
      <UnitTree
        tree={tree}
        selectedId="unit-eng"
        onSelect={vi.fn()}
        defaultExpanded={{ "tenant-acme": true }}
      />,
    );
    expect(screen.getByTestId("tree-row-unit-eng")).toHaveAttribute(
      "aria-selected",
      "true",
    );
    expect(screen.getByTestId("tree-row-tenant-acme")).toHaveAttribute(
      "aria-selected",
      "false",
    );
  });

  it("emits aria-expanded only on rows that have children", () => {
    render(
      <UnitTree
        tree={tree}
        selectedId="tenant-acme"
        onSelect={vi.fn()}
        defaultExpanded={{ "tenant-acme": true, "unit-eng": false }}
      />,
    );
    expect(screen.getByTestId("tree-row-tenant-acme")).toHaveAttribute(
      "aria-expanded",
      "true",
    );
    expect(screen.getByTestId("tree-row-unit-eng")).toHaveAttribute(
      "aria-expanded",
      "false",
    );
    // Leaf unit (no children) should NOT carry aria-expanded.
    expect(screen.getByTestId("tree-row-unit-research")).not.toHaveAttribute(
      "aria-expanded",
    );
  });

  it("encodes depth via aria-level (1-indexed)", () => {
    render(
      <UnitTree
        tree={tree}
        selectedId="tenant-acme"
        onSelect={vi.fn()}
        defaultExpanded={{ "tenant-acme": true, "unit-eng": true }}
      />,
    );
    expect(screen.getByTestId("tree-row-tenant-acme")).toHaveAttribute(
      "aria-level",
      "1",
    );
    expect(screen.getByTestId("tree-row-unit-eng")).toHaveAttribute(
      "aria-level",
      "2",
    );
    expect(screen.getByTestId("tree-row-agent-ada")).toHaveAttribute(
      "aria-level",
      "3",
    );
  });

  it("dispatches onSelect when a row is clicked", () => {
    const onSelect = vi.fn();
    render(
      <UnitTree
        tree={tree}
        selectedId="tenant-acme"
        onSelect={onSelect}
        defaultExpanded={{ "tenant-acme": true }}
      />,
    );
    fireEvent.click(screen.getByTestId("tree-row-unit-eng"));
    expect(onSelect).toHaveBeenCalledWith("unit-eng");
  });

  it("toggles expansion via the twisty without changing selection", () => {
    const onSelect = vi.fn();
    render(
      <UnitTree
        tree={tree}
        selectedId="tenant-acme"
        onSelect={onSelect}
        defaultExpanded={{ "tenant-acme": true, "unit-eng": false }}
      />,
    );
    // Children of unit-eng are hidden initially.
    expect(screen.queryByTestId("tree-row-agent-ada")).toBeNull();
    fireEvent.click(screen.getByTestId("tree-twisty-unit-eng"));
    // Children appear; selection did not change.
    expect(screen.getByTestId("tree-row-agent-ada")).toBeInTheDocument();
    expect(onSelect).not.toHaveBeenCalled();
    expect(screen.getByTestId("tree-row-unit-eng")).toHaveAttribute(
      "aria-expanded",
      "true",
    );
  });

  it("paints the worst-status descendant on a collapsed branch's status dot", () => {
    render(
      <UnitTree
        tree={tree}
        selectedId="tenant-acme"
        onSelect={vi.fn()}
        defaultExpanded={{ "tenant-acme": true, "unit-eng": false }}
      />,
    );
    // unit-eng is "running" but contains an agent with status="error" — the
    // collapsed dot should surface that error so operators see it without
    // expanding.
    const dot = screen.getByTestId("tree-status-dot-unit-eng");
    expect(dot).toHaveAttribute("data-status", "error");
  });

  it("paints the row's own status when the branch is expanded", () => {
    render(
      <UnitTree
        tree={tree}
        selectedId="tenant-acme"
        onSelect={vi.fn()}
        defaultExpanded={{ "tenant-acme": true, "unit-eng": true }}
      />,
    );
    const dot = screen.getByTestId("tree-status-dot-unit-eng");
    expect(dot).toHaveAttribute("data-status", "running");
  });

  describe("keyboard navigation (V21-tree-keyboard)", () => {
    // Local fixture so keyboard-test mutation of expansion state can't
    // leak into the colocated ARIA tests above.
    const kbTree: TreeNode = {
      id: "tenant-root",
      name: "Tenant",
      kind: "Tenant",
      status: "running",
      children: [
        {
          id: "unit-alpha",
          name: "Alpha",
          kind: "Unit",
          status: "running",
          children: [
            { id: "agent-anna", name: "Anna", kind: "Agent", status: "running" },
            { id: "agent-arno", name: "Arno", kind: "Agent", status: "running" },
          ],
        },
        {
          id: "unit-beta",
          name: "Beta",
          kind: "Unit",
          status: "running",
        },
      ],
    };

    function renderKb(selectedId = "tenant-root") {
      return render(
        <UnitTree
          tree={kbTree}
          selectedId={selectedId}
          onSelect={vi.fn()}
          defaultExpanded={{ "tenant-root": true, "unit-alpha": true }}
        />,
      );
    }

    it("ArrowDown moves focus to the next visible row", () => {
      renderKb();
      const start = screen.getByTestId("tree-row-tenant-root");
      start.focus();
      fireEvent.keyDown(start, { key: "ArrowDown" });
      expect(document.activeElement).toBe(
        screen.getByTestId("tree-row-unit-alpha"),
      );
    });

    it("ArrowUp moves focus to the previous visible row", () => {
      renderKb();
      const from = screen.getByTestId("tree-row-unit-alpha");
      from.focus();
      fireEvent.keyDown(from, { key: "ArrowUp" });
      expect(document.activeElement).toBe(
        screen.getByTestId("tree-row-tenant-root"),
      );
    });

    it("ArrowRight on a closed branch expands it (focus stays)", () => {
      render(
        <UnitTree
          tree={kbTree}
          selectedId="unit-alpha"
          onSelect={vi.fn()}
          defaultExpanded={{ "tenant-root": true, "unit-alpha": false }}
        />,
      );
      const row = screen.getByTestId("tree-row-unit-alpha");
      row.focus();
      // Children hidden initially.
      expect(screen.queryByTestId("tree-row-agent-anna")).toBeNull();
      fireEvent.keyDown(row, { key: "ArrowRight" });
      expect(
        screen.getByTestId("tree-row-unit-alpha"),
      ).toHaveAttribute("aria-expanded", "true");
      expect(screen.getByTestId("tree-row-agent-anna")).toBeInTheDocument();
    });

    it("ArrowRight on an open branch moves to the first child", () => {
      renderKb();
      const row = screen.getByTestId("tree-row-unit-alpha");
      row.focus();
      fireEvent.keyDown(row, { key: "ArrowRight" });
      expect(document.activeElement).toBe(
        screen.getByTestId("tree-row-agent-anna"),
      );
    });

    it("ArrowLeft on an open branch collapses it (focus stays)", () => {
      renderKb();
      const row = screen.getByTestId("tree-row-unit-alpha");
      row.focus();
      fireEvent.keyDown(row, { key: "ArrowLeft" });
      expect(
        screen.getByTestId("tree-row-unit-alpha"),
      ).toHaveAttribute("aria-expanded", "false");
    });

    it("ArrowLeft on a leaf moves focus to the parent", () => {
      renderKb();
      const row = screen.getByTestId("tree-row-agent-anna");
      row.focus();
      fireEvent.keyDown(row, { key: "ArrowLeft" });
      expect(document.activeElement).toBe(
        screen.getByTestId("tree-row-unit-alpha"),
      );
    });

    it("Home focuses the first visible row", () => {
      renderKb();
      const row = screen.getByTestId("tree-row-agent-arno");
      row.focus();
      fireEvent.keyDown(row, { key: "Home" });
      expect(document.activeElement).toBe(
        screen.getByTestId("tree-row-tenant-root"),
      );
    });

    it("End focuses the last visible row", () => {
      renderKb();
      const row = screen.getByTestId("tree-row-tenant-root");
      row.focus();
      fireEvent.keyDown(row, { key: "End" });
      expect(document.activeElement).toBe(
        screen.getByTestId("tree-row-unit-beta"),
      );
    });

    it("Enter dispatches onSelect for the focused row", () => {
      const onSelect = vi.fn();
      render(
        <UnitTree
          tree={kbTree}
          selectedId="tenant-root"
          onSelect={onSelect}
          defaultExpanded={{ "tenant-root": true, "unit-alpha": true }}
        />,
      );
      const row = screen.getByTestId("tree-row-unit-beta");
      row.focus();
      fireEvent.keyDown(row, { key: "Enter" });
      expect(onSelect).toHaveBeenCalledWith("unit-beta");
    });

    it("Space dispatches onSelect for the focused row", () => {
      const onSelect = vi.fn();
      render(
        <UnitTree
          tree={kbTree}
          selectedId="tenant-root"
          onSelect={onSelect}
          defaultExpanded={{ "tenant-root": true, "unit-alpha": true }}
        />,
      );
      const row = screen.getByTestId("tree-row-agent-anna");
      row.focus();
      fireEvent.keyDown(row, { key: " " });
      expect(onSelect).toHaveBeenCalledWith("agent-anna");
    });

    it("type-ahead moves focus to the next row whose label starts with the typed prefix", () => {
      renderKb();
      const row = screen.getByTestId("tree-row-tenant-root");
      row.focus();
      // "b" should jump to Beta (the only visible row starting with 'b').
      fireEvent.keyDown(row, { key: "b" });
      expect(document.activeElement).toBe(
        screen.getByTestId("tree-row-unit-beta"),
      );
    });
  });

  it("surfaces a worst-status buried four levels deep on the collapsed top-level row", () => {
    // Fixture independent of the file-scoped `tree` above: a
    // Tenant → Unit → Unit → Unit → Agent(error) chain where only the leaf
    // agent is failing. When every branch is collapsed except the tenant
    // root, the top-level unit's dot must surface `error` so operators can
    // spot the buried failure without expanding.
    const deepTree: TreeNode = {
      id: "tenant-deep",
      name: "Deep Tenant",
      kind: "Tenant",
      status: "running",
      children: [
        {
          id: "unit-top",
          name: "Top",
          kind: "Unit",
          status: "running",
          children: [
            {
              id: "unit-mid",
              name: "Mid",
              kind: "Unit",
              status: "running",
              children: [
                {
                  id: "unit-inner",
                  name: "Inner",
                  kind: "Unit",
                  status: "running",
                  children: [
                    {
                      id: "agent-buried",
                      name: "Buried",
                      kind: "Agent",
                      status: "error",
                    },
                  ],
                },
              ],
            },
          ],
        },
      ],
    };
    render(
      <UnitTree
        tree={deepTree}
        selectedId="tenant-deep"
        onSelect={vi.fn()}
        defaultExpanded={{ "tenant-deep": true }}
      />,
    );
    const dot = screen.getByTestId("tree-status-dot-unit-top");
    expect(dot).toHaveAttribute("data-status", "error");
  });
});
