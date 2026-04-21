"use client";

// Agent Config tab (EXP-tab-agent-config, umbrella #815 §4).
//
// Surfaces the agent's execution block + expertise + daily budget knob
// — the same slots the legacy `/agents/[id]` Settings tab bundles. The
// Explorer quick view reuses the live components so behaviour stays
// consistent while the route is retired later by `DEL-agents`.

import { Settings } from "lucide-react";

import { AgentExecutionPanel } from "@/components/agents/tab-impls/execution-panel";
import { AgentExpertisePanel } from "@/components/expertise/agent-expertise-panel";
import { useAgent } from "@/lib/api/queries";

import type { AgentNode } from "../aggregate";

import { registerTab, type TabContentProps } from "./index";

function AgentConfigTab({ node }: TabContentProps) {
  // The Execution panel needs the owning unit id so it can overlay
  // inherited defaults. The TreeNode itself doesn't carry the parent
  // link as a strong field; pull it from the agent detail response.
  // Hook runs unconditionally — registry guarantees `kind === "Agent"`.
  const { data } = useAgent(node.id);
  if (node.kind !== "Agent") return null;
  const agent = node as AgentNode;
  const parentUnitId = data?.agent?.parentUnit ?? null;

  return (
    <div className="space-y-6" data-testid="tab-agent-config">
      <header className="flex items-center gap-2 text-sm text-muted-foreground">
        <Settings className="h-4 w-4" aria-hidden="true" />
        <span>
          Execution defaults and expertise claims for this agent. Mirrors
          the matching `spring agent …` CLI subcommands.
        </span>
      </header>

      <section className="space-y-2" aria-label="Execution">
        <h3 className="text-sm font-medium">Execution</h3>
        <AgentExecutionPanel
          agentId={agent.id}
          parentUnitId={parentUnitId}
        />
      </section>

      <section className="space-y-2" aria-label="Expertise">
        <h3 className="text-sm font-medium">Expertise</h3>
        <AgentExpertisePanel agentId={agent.id} />
      </section>
    </div>
  );
}

registerTab("Agent", "Config", AgentConfigTab);

export default AgentConfigTab;
