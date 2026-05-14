"use client";

// Agent Traces tab — thin wrapper around the unified `<TracesTab>`.
// The canonical control (#2272) accepts a `kind + id` pair so both
// subjects render the same control + share the same fixture.
// Per canonical-tabs.md § 4 row 7 / § 5.7.

import { TracesTab } from "@/components/units/tab-impls/traces-tab";

import { registerTab, type TabContentProps } from "./index";

function AgentTracesTab({ node }: TabContentProps) {
  if (node.kind !== "Agent") return null;
  return <TracesTab kind="Agent" id={node.id} />;
}

registerTab("Agent", "Traces", AgentTracesTab);

export default AgentTracesTab;
