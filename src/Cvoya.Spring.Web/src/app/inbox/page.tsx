// Inbox — § 3.4 of `docs/design/portal-exploration.md`. The full
// surface is tracked by #447; this placeholder anchors the sidebar and
// breadcrumbs so deep links never 404.
import { Inbox } from "lucide-react";

import { RoutePlaceholder } from "@/components/route-placeholder";

export default function InboxPage() {
  return (
    <RoutePlaceholder
      title="Inbox"
      description="Conversations awaiting a response from you."
      icon={Inbox}
      tracking={[{ number: 447, label: "Inbox surface (portal)" }]}
      related={[
        { href: "/conversations", label: "Browse all conversations" },
        { href: "/activity", label: "Activity stream" },
      ]}
    />
  );
}
