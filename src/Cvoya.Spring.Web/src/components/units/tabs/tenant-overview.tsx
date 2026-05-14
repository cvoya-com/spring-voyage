"use client";

// Tenant Overview tab (EXP-tab-tenant, umbrella #815 §4).
// Thin wrapper around the unified `<OverviewTab>` — same chrome and
// description placement shared with the unit and agent surfaces.
// The tenant variant renders the top-level UnitCard grid (or the
// no-units empty state) inside the canonical body. The unified
// component (#2258) accepts a `kind + node` pair so all three
// subjects render through the same control.

import { OverviewTab } from "@/components/units/tab-impls/overview-tab";

import { registerTab, type TabContentProps } from "./index";

function TenantOverviewTab({ node }: TabContentProps) {
  if (node.kind !== "Tenant") return null;
  return <OverviewTab kind="Tenant" node={node} />;
}

registerTab("Tenant", "Overview", TenantOverviewTab);

export default TenantOverviewTab;
