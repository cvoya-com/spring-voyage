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
});
