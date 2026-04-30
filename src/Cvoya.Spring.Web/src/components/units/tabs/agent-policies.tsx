"use client";

// Agent Policies tab (EXP-tab-agent-policies, umbrella #815 §2 + §4,
// issues #934 and #534).
//
// The user explicitly chose the "Policies" placement for the agent
// initiative editor (symmetrical with the Unit Policies tab, matches
// the §2 literal wording). Two sections render here:
//   1. Initiative — per-agent initiative policy (issue #934).
//   2. Cloning policy — persistent cloning constraints (issue #534).
// Other dimensions (cost, model, skill) are declared on the owning unit.

import { Shield } from "lucide-react";

import { AgentCloningPolicyPanel } from "@/components/agents/agent-cloning-policy-panel";
import { AgentInitiativePanel } from "@/components/agents/agent-initiative-panel";

import { registerTab, type TabContentProps } from "./index";

function AgentPoliciesTab({ node }: TabContentProps) {
  if (node.kind !== "Agent") return null;

  return (
    <div className="space-y-6" data-testid="tab-agent-policies">
      <header className="flex items-center gap-2 text-sm text-muted-foreground">
        <Shield className="h-4 w-4" aria-hidden="true" />
        <span>
          Policy overrides declared by this agent. Cost, model, and skill
          dimensions are declared on the owning unit.
        </span>
      </header>

      <section className="space-y-2" aria-label="Initiative">
        <h3 className="text-sm font-medium">Initiative</h3>
        <AgentInitiativePanel agentId={node.id} />
      </section>

      <section
        className="space-y-2"
        aria-label="Cloning policy"
        data-testid="tab-agent-policies-cloning"
      >
        <h3 className="text-sm font-medium">Cloning policy</h3>
        <AgentCloningPolicyPanel agentId={node.id} />
      </section>
    </div>
  );
}

registerTab("Agent", "Policies", AgentPoliciesTab);

export default AgentPoliciesTab;
