// Analytics → Throughput — § 5.7 of `docs/design/portal-exploration.md`.
// Tracked by #448. Placeholder surface until the widgets ship.
import { BarChart3 } from "lucide-react";

import { RoutePlaceholder } from "@/components/route-placeholder";

export default function AnalyticsThroughputPage() {
  return (
    <RoutePlaceholder
      title="Throughput"
      description="Interaction counts over time — messages per agent, turns per unit, tool calls by type."
      icon={BarChart3}
      tracking={[
        { number: 448, label: "Analytics surface (Costs / Throughput / Waits)" },
      ]}
      related={[
        { href: "/analytics/costs", label: "Costs" },
        { href: "/activity", label: "Raw activity stream" },
      ]}
    />
  );
}
