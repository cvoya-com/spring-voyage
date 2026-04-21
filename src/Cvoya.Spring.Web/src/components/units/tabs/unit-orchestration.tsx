"use client";

// Unit Orchestration tab (EXP-tab-unit-orchestration, umbrella #815 §4).
// Thin adapter around the legacy `<OrchestrationTab>` so the Explorer
// surface and `/units/[id]` keep the same orchestration UI during the
// V2 rollout. `DEL-units-id` retires the legacy host later.

import { OrchestrationTab } from "@/components/units/tab-impls/orchestration-tab";

import { registerTab, type TabContentProps } from "./index";

function UnitOrchestrationTab({ node }: TabContentProps) {
  if (node.kind !== "Unit") return null;
  return <OrchestrationTab unitId={node.id} />;
}

registerTab("Unit", "Orchestration", UnitOrchestrationTab);

export default UnitOrchestrationTab;
