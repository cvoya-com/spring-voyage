"use client";

// Agent Memory tab — thin wrapper around the unified `<MemoryTab>`.
// Same body the unit surface uses; the unified component (#2257)
// accepts a `kind + id` pair so both subjects render the same control.
// Per canonical-tabs.md § 4 row 4 / § 5.4.

import { MemoryTab } from "@/components/units/tab-impls/memory-tab";

import { registerTab, type TabContentProps } from "./index";

function AgentMemoryTab({ node }: TabContentProps) {
  if (node.kind !== "Agent") return null;
  return <MemoryTab kind="Agent" id={node.id} />;
}

registerTab("Agent", "Memory", AgentMemoryTab);

export default AgentMemoryTab;
