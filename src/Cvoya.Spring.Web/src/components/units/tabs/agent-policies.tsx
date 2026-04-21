"use client";

// Agent Policies tab (EXP-tab-agent-policies, umbrella #815 §2 + §4,
// issue #934).
//
// The user explicitly chose the "Policies" placement for the agent
// initiative editor (symmetrical with the Unit Policies tab, matches
// the §2 literal wording). Only Initiative renders here today — cost
// and model caps live under the owning unit. If future per-agent
// policy dimensions land, they'll stack below Initiative here.

import { Shield } from "lucide-react";

import { AgentInitiativePanel } from "@/components/agents/agent-initiative-panel";

import { registerTab, type TabContentProps } from "./index";

function AgentPoliciesTab({ node }: TabContentProps) {
  if (node.kind !== "Agent") return null;

  return (
    <div className="space-y-6" data-testid="tab-agent-policies">
      <header className="flex items-center gap-2 text-sm text-muted-foreground">
        <Shield className="h-4 w-4" aria-hidden="true" />
        <span>
          Policy overrides declared by this agent. Only Initiative is a
          per-agent axis today; other dimensions (cost, model, skill)
          are declared on the owning unit.
        </span>
      </header>

      <AgentInitiativePanel agentId={node.id} />
    </div>
  );
}

registerTab("Agent", "Policies", AgentPoliciesTab);

export default AgentPoliciesTab;
