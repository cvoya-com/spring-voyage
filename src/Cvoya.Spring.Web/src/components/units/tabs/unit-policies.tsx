"use client";

// Unit Policies tab (#411; unified under #2255).
// Thin wrapper around the canonical `<PoliciesTab>` — same dimension
// editor (Skill / Model / Cost / ExecutionMode / Initiative) + Effective
// footer + cross-link to `/policies` shared with the tenant and agent
// surfaces. The unified component accepts a `{ kind, id }` pair so all
// three subjects render the same control (see
// `docs/design/canonical-tabs.md` § 5.9).

import { PoliciesTab } from "@/components/units/tab-impls/policies-tab";

import { registerTab, type TabContentProps } from "./index";

function UnitPoliciesTab({ node }: TabContentProps) {
  if (node.kind !== "Unit") return null;
  return <PoliciesTab kind="Unit" id={node.id} />;
}

registerTab("Unit", "Policies", UnitPoliciesTab);

export default UnitPoliciesTab;
