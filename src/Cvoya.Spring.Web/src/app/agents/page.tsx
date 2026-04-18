// Agents list — § 3.2 / § 5.2 of `docs/design/portal-exploration.md`.
// The full first-class lens (filters, saved views, status rail) is
// tracked by #450; this placeholder anchors the sidebar and breadcrumbs
// so `/agents` never 404s. The per-agent detail page lives at
// `/agents/[id]/page.tsx` and is fully functional today.
import { Users } from "lucide-react";

import { RoutePlaceholder } from "@/components/route-placeholder";

export default function AgentsIndexPage() {
  return (
    <RoutePlaceholder
      title="Agents"
      description="Every agent across every unit."
      icon={Users}
      tracking={[{ number: 450, label: "Agents first-class lens (portal)" }]}
      related={[
        { href: "/units", label: "Browse by unit" },
        { href: "/", label: "Dashboard agent cards" },
      ]}
    />
  );
}
