"use client";

// Unit Policies tab (EXP-tab-unit-policies, umbrella #815 §4).
// Adapter around the legacy `<PoliciesTab>` so the Explorer surface and
// `/units/[id]` use the same policy UI during the V2 rollout.

import { PoliciesTab } from "@/components/units/tab-impls/policies-tab";

import { registerTab, type TabContentProps } from "./index";

function UnitPoliciesTab({ node }: TabContentProps) {
  if (node.kind !== "Unit") return null;
  return <PoliciesTab unitId={node.id} />;
}

registerTab("Unit", "Policies", UnitPoliciesTab);

export default UnitPoliciesTab;
