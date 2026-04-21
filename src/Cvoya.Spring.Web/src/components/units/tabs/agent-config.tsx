"use client";

// Agent Config tab (EXP-tab-agent-config, umbrella #815 §4).
//
// Surfaces the agent's execution block + expertise + daily budget knob
// + an Advanced/Debug section — the same slots the legacy
// `/agents/[id]` Settings + Advanced tabs bundled. The Explorer quick
// view reuses the live components so behaviour stays consistent while
// the route is retired later by `DEL-agents`.
//
// Initiative lives on the separate Policies tab (issue #934) —
// symmetrical with the Unit Policies tab, matches §2 literal wording.

import { Settings } from "lucide-react";

import { AgentBudgetPanel } from "@/components/agents/agent-budget-panel";
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
  const status = data?.status;

  return (
    <div className="space-y-6" data-testid="tab-agent-config">
      <header className="flex items-center gap-2 text-sm text-muted-foreground">
        <Settings className="h-4 w-4" aria-hidden="true" />
        <span>
          Execution defaults, daily budget, and expertise claims for this
          agent. Mirrors the matching `spring agent …` CLI subcommands.
          Initiative lives on the Policies tab.
        </span>
      </header>

      <section className="space-y-2" aria-label="Execution">
        <h3 className="text-sm font-medium">Execution</h3>
        <AgentExecutionPanel agentId={agent.id} parentUnitId={parentUnitId} />
      </section>

      <section className="space-y-2" aria-label="Budget">
        <h3 className="text-sm font-medium">Daily budget</h3>
        <AgentBudgetPanel agentId={agent.id} />
      </section>

      <section className="space-y-2" aria-label="Expertise">
        <h3 className="text-sm font-medium">Expertise</h3>
        <AgentExpertisePanel agentId={agent.id} />
      </section>

      <DebugSection status={status} />
    </div>
  );
}

/**
 * Collapsible Advanced/Debug section (#935). Renders the raw status
 * payload from the agent detail response as JSON. Defaulted to
 * collapsed so typical operators see a clean Config tab; keyboard +
 * screen-reader users toggle it via the native `<details>` affordance.
 */
function DebugSection({ status }: { status: unknown }) {
  // Stringify defensively — `status` is typed as `JsonElement | null`,
  // and the generated schema leaves it widely open. Using a replacer
  // that falls back on object-like values keeps us safe from circular
  // refs (none are expected server-side, but defence in depth is
  // cheap).
  const pretty = (() => {
    if (status == null) return "(no status reported)";
    try {
      return JSON.stringify(status, null, 2);
    } catch {
      return String(status);
    }
  })();

  return (
    <section aria-label="Debug">
      <details
        className="group rounded-md border border-border"
        data-testid="agent-debug-section"
      >
        <summary className="flex cursor-pointer items-center gap-2 px-3 py-2 text-sm font-medium">
          <span>Debug</span>
          <span className="text-xs text-muted-foreground">
            Raw status payload
          </span>
        </summary>
        <pre
          className="max-h-96 overflow-auto whitespace-pre-wrap border-t border-border bg-muted/40 p-3 font-mono text-xs"
          data-testid="agent-debug-status"
        >
          {pretty}
        </pre>
      </details>
    </section>
  );
}

registerTab("Agent", "Config", AgentConfigTab);

export default AgentConfigTab;
