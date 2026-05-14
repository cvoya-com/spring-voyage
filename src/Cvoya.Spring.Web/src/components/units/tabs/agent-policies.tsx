"use client";

// Agent Policies tab (#934 + #534; unified under #2255).
// Thin wrapper around the canonical `<PoliciesTab>` — same chrome as the
// unit and tenant surfaces, but the agent subject scopes to Initiative +
// Cloning only (Cost / Model / Skill / ExecutionMode dimensions are
// declared on the owning unit by design; see
// `docs/design/canonical-tabs.md` § 5.9).

import { PoliciesTab } from "@/components/units/tab-impls/policies-tab";

import { registerTab, type TabContentProps } from "./index";

function AgentPoliciesTab({ node }: TabContentProps) {
  if (node.kind !== "Agent") return null;
  return <PoliciesTab kind="Agent" id={node.id} />;
}

registerTab("Agent", "Policies", AgentPoliciesTab);

export default AgentPoliciesTab;
