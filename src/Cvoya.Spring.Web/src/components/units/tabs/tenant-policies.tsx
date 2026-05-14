"use client";

// Tenant Policies tab (umbrella #815 §4; unified under #2255).
// Thin wrapper around the canonical `<PoliciesTab>` — the tenant subject
// renders read-only summaries of every dimension (Skill / Model / Cost /
// ExecutionMode / Initiative as "set via CLI" placeholders where no
// tenant-scope read endpoint exists yet, plus the Cloning summary
// against the existing tenant cloning-policy endpoint) and preserves
// the deep-link to `/policies` for the cross-unit roll-up. See
// `docs/design/canonical-tabs.md` § 5.9.

import { PoliciesTab } from "@/components/units/tab-impls/policies-tab";

import { registerTab, type TabContentProps } from "./index";

function TenantPoliciesTab({ node }: TabContentProps) {
  if (node.kind !== "Tenant") return null;
  return <PoliciesTab kind="Tenant" id={node.id} />;
}

registerTab("Tenant", "Policies", TenantPoliciesTab);

export default TenantPoliciesTab;
