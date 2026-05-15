"use client";

// Agent Config tab (#2254). Thin wrapper around the canonical
// `<ConfigTab>` — same chrome and sub-tab strip the Tenant and Unit
// surfaces use. The previous stacked-section layout (Execution /
// Budget / Expertise / Debug rendered vertically) is replaced with a
// sub-tab strip that brings Agent × Config into parity with Unit ×
// Config; in the same move the alignment adds three sub-tabs
// (Connector, Skills, Secrets) so the per-subject sub-tab catalog
// reads consistently across subjects.
//
// `useAgent(id)` resolves the parent unit id (for the Execution
// panel's inherited-defaults overlay) and the raw status payload
// (surfaced inside the Debug sub-tab). Both lookups live in this
// wrapper — not in the canonical control — so the Tenant and Unit
// subjects don't pay for an agent-detail query they would never
// consume.

import { ConfigTab } from "@/components/units/tab-impls/config-tab";
import { useAgent } from "@/lib/api/queries";

import type { AgentNode } from "../aggregate";

import { registerTab, type TabContentProps } from "./index";

function AgentConfigTab({ node }: TabContentProps) {
  // Hook runs unconditionally — registry guarantees `kind === "Agent"`.
  const { data } = useAgent(node.id);
  if (node.kind !== "Agent") return null;
  const agent = node as AgentNode;
  const parentUnitId = data?.agent?.parentUnit ?? null;
  const status = data?.status;

  return (
    <ConfigTab
      kind="Agent"
      id={agent.id}
      name={agent.name}
      parentUnitId={parentUnitId}
      status={status}
    />
  );
}

registerTab("Agent", "Config", AgentConfigTab);

export default AgentConfigTab;
