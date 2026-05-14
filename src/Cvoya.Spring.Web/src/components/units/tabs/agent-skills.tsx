"use client";

// Agent Skills tab — thin wrapper around the unified
// `<EquippedSkillsTab>`. The canonical control (#2271) accepts a
// `kind + id` pair so both subjects render the same control.
// Per canonical-tabs.md § 4 row 6 / § 5.6.

import { EquippedSkillsTab } from "@/components/units/tab-impls/equipped-skills-tab";

import { registerTab, type TabContentProps } from "./index";

function AgentSkillsTab({ node }: TabContentProps) {
  if (node.kind !== "Agent") return null;
  return <EquippedSkillsTab kind="Agent" id={node.id} name={node.name} />;
}

registerTab("Agent", "Skills", AgentSkillsTab);

export default AgentSkillsTab;
