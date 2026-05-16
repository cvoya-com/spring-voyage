"use client";

// Unit Skills tab — thin wrapper around the unified
// `<EquippedSkillsTab>`. The canonical control (#2271) accepts a
// `kind + id` pair so both subjects render the same control.
//
// Per canonical-tabs.md § 4 row 6 / § 5.6 and
// `docs/concepts/units-vs-agents.md` rule 3 — a unit is an agent and
// the Skills surface applies identically to both subjects. The body
// now drives the real per-subject equipped-skill store endpoints from
// #2360 (Unit feeds Layer 2 of the assembled prompt).

import { EquippedSkillsTab } from "@/components/units/tab-impls/equipped-skills-tab";

import { registerTab, type TabContentProps } from "./index";

function UnitSkillsTab({ node }: TabContentProps) {
  if (node.kind !== "Unit") return null;
  return <EquippedSkillsTab kind="Unit" id={node.id} name={node.name} />;
}

registerTab("Unit", "Skills", UnitSkillsTab);

export default UnitSkillsTab;
