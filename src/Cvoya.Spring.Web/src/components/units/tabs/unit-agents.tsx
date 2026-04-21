"use client";

// Unit Agents tab (EXP-tab-unit-agents, umbrella #815 §4).
//
// Port of `app/units/[id]/agents-tab.tsx`. The legacy tab takes
// `{ unitId: string }` — we forward `node.id` since the Explorer's
// `TreeNode.id` is the same identifier the legacy API treats as
// `unitId`. See `app/units/[id]/unit-config-client.tsx` for the
// original usage.

import { AgentsTab } from "@/app/units/[id]/agents-tab";

import { registerTab, type TabContentProps } from "./index";

function UnitAgentsTab({ node }: TabContentProps) {
  if (node.kind !== "Unit") return null;
  return <AgentsTab unitId={node.id} />;
}

registerTab("Unit", "Agents", UnitAgentsTab);

export default UnitAgentsTab;
