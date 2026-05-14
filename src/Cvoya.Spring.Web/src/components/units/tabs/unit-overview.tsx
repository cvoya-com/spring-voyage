"use client";

// Unit Overview tab (EXP-tab-unit-overview, umbrella #815 §4).
// Thin wrapper around the unified `<OverviewTab>` — same chrome,
// same description placement, same stat-tile / sparkline / expertise
// composition shared with the agent and tenant surfaces. The unified
// component (#2258) accepts a `kind + node` pair so all three
// subjects render through the same control.

import { OverviewTab } from "@/components/units/tab-impls/overview-tab";

import { registerTab, type TabContentProps } from "./index";

function UnitOverviewTab({ node }: TabContentProps) {
  if (node.kind !== "Unit") return null;
  return <OverviewTab kind="Unit" node={node} />;
}

registerTab("Unit", "Overview", UnitOverviewTab);

export default UnitOverviewTab;
