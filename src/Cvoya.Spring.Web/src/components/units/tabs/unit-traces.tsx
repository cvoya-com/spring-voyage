"use client";

// Unit Traces tab — thin wrapper around the unified `<TracesTab>`.
// The canonical control (#2272) accepts a `kind + id` pair so both
// subjects render the same control + share the same fixture. The
// fixture-vs-real-endpoint caveat applies identically to Unit:
// real traces land in V21-traces-api.
//
// Per canonical-tabs.md § 4 row 7 / § 5.7 and
// `docs/concepts/units-vs-agents.md` rule 3 — a unit is an agent and
// the Traces surface applies identically to both subjects.

import { TracesTab } from "@/components/units/tab-impls/traces-tab";

import { registerTab, type TabContentProps } from "./index";

function UnitTracesTab({ node }: TabContentProps) {
  if (node.kind !== "Unit") return null;
  return <TracesTab kind="Unit" id={node.id} />;
}

registerTab("Unit", "Traces", UnitTracesTab);

export default UnitTracesTab;
