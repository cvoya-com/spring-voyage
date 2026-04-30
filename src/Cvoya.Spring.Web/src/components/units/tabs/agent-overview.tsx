"use client";

// Agent Overview tab (EXP-tab-agent-overview, umbrella #815 §4).
//
// Surfaces the persistent-agent lifecycle controls from
// `app/agents/[id]/lifecycle-panel.tsx` plus a compact cost summary
// tile so the Explorer's primary agent landing has the
// deploy/undeploy/scale verbs one click away. The legacy agent
// detail page (which this tab replaces) also leads with lifecycle +
// cost; we preserve that emphasis here.

import Link from "next/link";
import { DollarSign } from "lucide-react";

import { LifecyclePanel } from "@/components/agents/tab-impls/lifecycle-panel";
import {
  Card,
  CardContent,
  CardHeader,
  CardTitle,
} from "@/components/ui/card";
import { useAgentCost } from "@/lib/api/queries";
import { formatCost } from "@/lib/utils";

import type { AgentNode } from "../aggregate";

import { registerTab, type TabContentProps } from "./index";

function AgentOverviewTab({ node }: TabContentProps) {
  // Hook runs unconditionally — registry guarantees `kind === "Agent"`.
  const costQuery = useAgentCost(node.id);
  if (node.kind !== "Agent") return null;
  const agent = node as AgentNode;
  const cost = costQuery.data ?? null;

  return (
    <div className="space-y-6" data-testid="tab-agent-overview">
      {agent.desc ? (
        <p className="text-sm text-muted-foreground">{agent.desc}</p>
      ) : null}

      <LifecyclePanel agentId={agent.id} />

      <Card>
        <CardHeader>
          <CardTitle className="flex items-center gap-2">
            <DollarSign className="h-4 w-4" aria-hidden="true" /> Cost summary
          </CardTitle>
        </CardHeader>
        <CardContent className="space-y-2 text-sm">
          {cost === null ? (
            <p
              className="text-muted-foreground"
              data-testid="tab-agent-overview-cost-empty"
            >
              No cost data yet for this agent.
            </p>
          ) : (
            <>
              <Row label="Total" value={formatCost(cost.totalCost)} />
              <Row
                label="Input tokens"
                value={cost.totalInputTokens.toLocaleString()}
              />
              <Row
                label="Output tokens"
                value={cost.totalOutputTokens.toLocaleString()}
              />
              <Row label="Records" value={cost.recordCount.toString()} />
            </>
          )}
        </CardContent>
      </Card>
      {/* Cross-portal link to the engagement portal for this agent.
          Per ADR-0033 rule 6: cross-portal navigation is a standard anchor. */}
      <p className="text-xs text-muted-foreground" data-testid="agent-overview-engagement-link-row">
        <Link
          href={`/engagement/mine?agent=${encodeURIComponent(agent.id)}`}
          className="text-primary hover:underline"
          data-testid="agent-overview-engagement-link"
        >
          View engagements for this agent
        </Link>{" "}
        in the Engagement portal.
      </p>
    </div>
  );
}

function Row({ label, value }: { label: string; value: string }) {
  return (
    <div className="flex justify-between">
      <span className="text-muted-foreground">{label}</span>
      <span className="font-medium">{value}</span>
    </div>
  );
}

registerTab("Agent", "Overview", AgentOverviewTab);

export default AgentOverviewTab;
