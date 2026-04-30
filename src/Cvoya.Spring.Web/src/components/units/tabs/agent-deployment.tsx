"use client";

// Agent Deployment tab (EXP-tab-agent-deployment, #1119).
//
// Dedicated surface for the persistent-agent lifecycle verbs:
//   deploy / undeploy / scale / status / logs
//
// Mirrors `spring agent {deploy,undeploy,scale,logs}` 1:1 — every CLI
// verb is reachable from this tab. The Agent Overview tab embeds a
// compact LifecyclePanel for quick access; this tab is the full-fidelity
// view and the canonical deep-link target (e.g. from the AgentCard's
// "Deployment" quick-action chip: `/units?node=<id>&tab=Deployment`).
//
// Confirmation dialogs for destructive verbs (undeploy, scale-to-zero)
// live inside LifecyclePanel itself so both surfaces share the same
// guard — no separate copy is required here.

import { Activity } from "lucide-react";

import { LifecyclePanel } from "@/components/agents/tab-impls/lifecycle-panel";

import type { AgentNode } from "../aggregate";

import { registerTab, type TabContentProps } from "./index";

function AgentDeploymentTab({ node }: TabContentProps) {
  // Registry guarantees `kind === "Agent"` — guard defensively so type
  // narrowing works for the AgentNode cast below.
  if (node.kind !== "Agent") return null;
  const agent = node as AgentNode;

  return (
    <div className="space-y-6" data-testid="tab-agent-deployment">
      <header className="flex items-center gap-2 text-sm text-muted-foreground">
        <Activity className="h-4 w-4" aria-hidden="true" />
        <span>
          Persistent container lifecycle for this agent. Mirrors{" "}
          <code className="rounded bg-muted px-1 py-0.5 font-mono text-xs">
            spring agent deploy / undeploy / scale / logs
          </code>
          . Destructive actions (undeploy, scale to 0) require confirmation.
        </span>
      </header>

      <LifecyclePanel agentId={agent.id} />
    </div>
  );
}

registerTab("Agent", "Deployment", AgentDeploymentTab);

export default AgentDeploymentTab;
