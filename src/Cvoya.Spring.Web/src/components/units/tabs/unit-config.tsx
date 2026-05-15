"use client";

// Unit Config tab (#2254). Thin wrapper around the canonical
// `<ConfigTab>` — same chrome and sub-tab strip the Tenant and Agent
// surfaces use. The existing sub-tabs (Boundary / Execution /
// Connector / Skills / Secrets / Expertise) are preserved; the
// alignment adds Budget + Debug sub-tabs to bring Unit × Config into
// parity with Agent × Config. Per units-vs-agents rule 3 a unit is an
// agent — the Budget sub-tab applies to both subjects.

import { ConfigTab } from "@/components/units/tab-impls/config-tab";

import { registerTab, type TabContentProps } from "./index";

function UnitConfigTab({ node }: TabContentProps) {
  if (node.kind !== "Unit") return null;
  return <ConfigTab kind="Unit" id={node.id} name={node.name} />;
}

registerTab("Unit", "Config", UnitConfigTab);

export default UnitConfigTab;
