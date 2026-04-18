// Policies — § 5.6 of `docs/design/portal-exploration.md`. A broader
// top-level surface that replaces `/initiative` and covers all five
// `UnitPolicy` dimensions (Skill / Model / Cost / ExecutionMode /
// Initiative). The per-unit editor already ships on
// `/units/[id]` → Policies tab (#411). The unified cross-unit index is
// tracked by #411; this placeholder anchors the sidebar and points at
// the surfaces that already work.
import { ShieldCheck } from "lucide-react";

import { RoutePlaceholder } from "@/components/route-placeholder";

export default function PoliciesIndexPage() {
  return (
    <RoutePlaceholder
      title="Policies"
      description="Skill, model, cost, execution, and initiative policies across every unit."
      icon={ShieldCheck}
      tracking={[
        { number: 411, label: "Unified Policies surface (portal)" },
      ]}
      related={[
        { href: "/units", label: "Per-unit Policies tab" },
        { href: "/initiative", label: "Per-agent initiative policy" },
      ]}
    />
  );
}
