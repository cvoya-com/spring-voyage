"use client";

// Agent Deployment tab — thin wrapper around the unified
// `<DeploymentTab>`. The canonical control (#2273) accepts a
// `kind + id` pair so both subjects render the same control.
// Per canonical-tabs.md § 4 row 12 / § 5.12.

import { DeploymentTab } from "@/components/units/tab-impls/deployment-tab";

import { registerTab, type TabContentProps } from "./index";

function AgentDeploymentTab({ node }: TabContentProps) {
  if (node.kind !== "Agent") return null;
  return <DeploymentTab kind="Agent" id={node.id} />;
}

registerTab("Agent", "Deployment", AgentDeploymentTab);

export default AgentDeploymentTab;
