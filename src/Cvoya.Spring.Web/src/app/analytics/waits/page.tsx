// Analytics → Wait times — § 5.7 of
// `docs/design/portal-exploration.md`. Tracked by #448. Placeholder
// surface until the rollups ship (relies on state-transition durations
// from the `StateChanged` activity events).
import { Clock } from "lucide-react";

import { RoutePlaceholder } from "@/components/route-placeholder";

export default function AnalyticsWaitsPage() {
  return (
    <RoutePlaceholder
      title="Wait times"
      description="Time-in-state rollups — idle, busy, waiting-on-human — per agent and per unit."
      icon={Clock}
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
