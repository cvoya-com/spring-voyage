"use client";

// Agent Activity tab (EXP-tab-agent-activity, umbrella #815 §4).
// Thin wrapper around the unified `<ActivityTab>` — same feed, same
// stream hook, same expandable-row affordance shared with the unit
// surface. The unified component (#2253) accepts a `kind + id` pair
// and renders the agent-only cost sparkline (#1363) + per-model
// breakdown (#1364) cards above the feed when `kind === "Agent"`.

import { ActivityTab } from "@/components/units/tab-impls/activity-tab";

import { registerTab, type TabContentProps } from "./index";

function AgentActivityTab({ node }: TabContentProps) {
  if (node.kind !== "Agent") return null;
  return <ActivityTab kind="Agent" id={node.id} />;
}

registerTab("Agent", "Activity", AgentActivityTab);

export default AgentActivityTab;
