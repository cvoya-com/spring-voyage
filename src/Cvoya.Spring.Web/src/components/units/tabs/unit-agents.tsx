"use client";

// Unit Agents tab (EXP-tab-unit-agents, umbrella #815 §4).
//
// Port of `app/units/[id]/agents-tab.tsx`. The legacy tab takes
// `{ unitId: string, unitDisplayName: string }` — we forward `node.id`
// since the Explorer's `TreeNode.id` is the same identifier the legacy
// API treats as `unitId`, and `node.name` for the create-dialog header.

import { AgentsTab } from "@/components/units/tab-impls/agents-tab";

import { registerTab, type TabContentProps } from "./index";

function UnitAgentsTab({ node }: TabContentProps) {
  if (node.kind !== "Unit") return null;
  return <AgentsTab unitId={node.id} unitDisplayName={node.name} />;
}

registerTab("Unit", "Agents", UnitAgentsTab);

export default UnitAgentsTab;
