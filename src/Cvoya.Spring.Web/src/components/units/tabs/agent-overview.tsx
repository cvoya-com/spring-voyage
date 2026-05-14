"use client";

// Agent Overview tab (EXP-tab-agent-overview, umbrella #815 §4).
// Thin wrapper around the unified `<OverviewTab>` — same chrome,
// same description placement, same lifecycle-embed + cost-summary +
// engagement-link composition shared with the unit and tenant
// surfaces. The unified component (#2258) accepts a `kind + node`
// pair so all three subjects render through the same control.

import { OverviewTab } from "@/components/units/tab-impls/overview-tab";

import { registerTab, type TabContentProps } from "./index";

function AgentOverviewTab({ node }: TabContentProps) {
  if (node.kind !== "Agent") return null;
  return <OverviewTab kind="Agent" node={node} />;
}

registerTab("Agent", "Overview", AgentOverviewTab);

export default AgentOverviewTab;
