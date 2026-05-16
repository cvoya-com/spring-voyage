"use client";

// Tenant Config tab (#2254 / canonical-tabs.md § 5.11). New tab —
// Tenant had no Config surface before this PR. The body is a thin
// wrapper around the canonical `<ConfigTab>` and re-uses the existing
// `/settings` panel bodies (`<TenantDefaultsPanel>`, `<BudgetPanel>`,
// `<CloningPolicyPanel>`) so there is exactly one canonical
// implementation of each panel — `/settings` and Tenant × Config
// render the same component bodies.
//
// Sub-tabs: Secrets (tenant-default credentials), Budget (daily-
// budget editor), Cloning (read-only summary; the editor still rides
// `spring agent clone policy set --scope tenant`). URL contract
// matches the Unit + Agent surfaces: `?tab=Config&subtab=<name>`.

import { ConfigTab } from "@/components/units/tab-impls/config-tab";

import { registerTab, type TabContentProps } from "./index";

function TenantConfigTab({ node }: TabContentProps) {
  if (node.kind !== "Tenant") return null;
  return <ConfigTab kind="Tenant" id={node.id} />;
}

registerTab("Tenant", "Config", TenantConfigTab);

export default TenantConfigTab;
