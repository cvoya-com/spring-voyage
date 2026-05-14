"use client";

// Unit Deployment tab — thin wrapper around the unified
// `<DeploymentTab>`. The canonical control (#2273) accepts a
// `kind + id` pair so both subjects render the same control.
//
// In v0.1 the Unit variant renders a "Deploy via CLI for now"
// placeholder because the lifecycle endpoints are strictly agent-keyed
// today; the canonical tab *position* is honored even with the
// placeholder body. Unit-keyed endpoints are tracked in #2274.
//
// Per canonical-tabs.md § 4 row 12 / § 5.12 and
// `docs/concepts/units-vs-agents.md` rule 3 — a unit is an agent and
// the Deployment surface applies identically to both subjects.

import { DeploymentTab } from "@/components/units/tab-impls/deployment-tab";

import { registerTab, type TabContentProps } from "./index";

function UnitDeploymentTab({ node }: TabContentProps) {
  if (node.kind !== "Unit") return null;
  return <DeploymentTab kind="Unit" id={node.id} />;
}

registerTab("Unit", "Deployment", UnitDeploymentTab);

export default UnitDeploymentTab;
