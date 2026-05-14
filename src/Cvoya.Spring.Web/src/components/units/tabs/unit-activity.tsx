"use client";

// Unit Activity tab (EXP-tab-unit-activity, umbrella #815 §4).
// Thin wrapper around the unified `<ActivityTab>` — same feed, same
// stream hook, same expandable-row affordance shared with the agent
// surface. The unified component (#2253) accepts a `kind + id` pair so
// both subjects render the same control.

import { ActivityTab } from "@/components/units/tab-impls/activity-tab";

import { registerTab, type TabContentProps } from "./index";

function UnitActivityTab({ node }: TabContentProps) {
  if (node.kind !== "Unit") return null;
  return <ActivityTab kind="Unit" id={node.id} />;
}

registerTab("Unit", "Activity", UnitActivityTab);

export default UnitActivityTab;
