"use client";

// Unit Memory tab — thin wrapper around the unified `<MemoryTab>`.
// Same body the agent surface uses; the unified component (#2257)
// accepts a `kind + id` pair so both subjects render the same control.
// Per canonical-tabs.md § 4 row 4 / § 5.4.

import { MemoryTab } from "@/components/units/tab-impls/memory-tab";

import { registerTab, type TabContentProps } from "./index";

function UnitMemoryTab({ node }: TabContentProps) {
  if (node.kind !== "Unit") return null;
  return <MemoryTab kind="Unit" id={node.id} />;
}

registerTab("Unit", "Memory", UnitMemoryTab);

export default UnitMemoryTab;
