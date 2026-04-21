import { fireEvent, render, screen } from "@testing-library/react";
import { afterEach, beforeEach, describe, expect, it } from "vitest";

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
});
