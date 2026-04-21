"use client";

// Unit Activity tab (EXP-tab-unit-activity, umbrella #815 §4).
// Adapter around the legacy `<ActivityTab>` — same feed, same hook,
// reused from the Explorer surface.

import { ActivityTab } from "@/app/units/[id]/activity-tab";

import { registerTab, type TabContentProps } from "./index";

function UnitActivityTab({ node }: TabContentProps) {
  if (node.kind !== "Unit") return null;
  return <ActivityTab unitId={node.id} />;
}

registerTab("Unit", "Activity", UnitActivityTab);

export default UnitActivityTab;
