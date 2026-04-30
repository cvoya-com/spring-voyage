// My engagements list (E2.4, #1416).
//
// URL: /engagement/mine
//
// Three cross-link URL shapes:
//   /engagement/mine                  — my engagements (human-participates threads)
//   /engagement/mine?unit=<id>        — all engagements for a specific unit
//   /engagement/mine?agent=<id>       — all engagements for a specific agent
//
// The management portal's unit-detail and agent-detail pages link here
// with the optional query param. A2A-only engagements are excluded from
// the default "mine" view but visible in the per-unit / per-agent slices
// (they can be observed read-only from the detail view).

import { MessagesSquare, Plus } from "lucide-react";
import Link from "next/link";
import type { Metadata } from "next";
import { EngagementList } from "@/components/engagement/engagement-list";

export const metadata: Metadata = {
  title: "My engagements — Spring Voyage",
};

interface MyEngagementsPageProps {
  searchParams: Promise<Record<string, string | undefined>>;
}

export default async function MyEngagementsPage({
  searchParams,
}: MyEngagementsPageProps) {
  const params = await searchParams;
  const unit = params.unit;
  const agent = params.agent;

  // Determine which slice to show based on the query params.
  const slice = unit ? "unit" : agent ? "agent" : "mine";

  const heading =
    slice === "unit"
      ? `Engagements for unit: ${unit}`
      : slice === "agent"
        ? `Engagements for agent: ${agent}`
        : "My engagements";

  const description =
    slice === "unit"
      ? "All engagements involving this unit, including agent-to-agent threads."
      : slice === "agent"
        ? "All engagements involving this agent, including agent-to-agent threads."
        : "Threads you are a participant in, sorted by latest activity.";

  return (
    <div className="space-y-6" data-testid="my-engagements-page">
      <div className="flex items-start justify-between gap-3">
        <div>
          <h1 className="flex items-center gap-2 text-2xl font-bold">
            <MessagesSquare className="h-5 w-5" aria-hidden="true" />
            {heading}
          </h1>
          <p className="mt-1 text-sm text-muted-foreground">{description}</p>
        </div>
        {slice === "mine" && (
          <Link
            href="/engagement/new"
            data-testid="engagement-mine-new-cta"
            className="inline-flex h-8 items-center justify-center gap-1 rounded-md bg-primary px-3 text-sm font-medium text-primary-foreground transition-colors hover:bg-primary/90 focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring focus-visible:ring-offset-2"
          >
            <Plus className="h-3.5 w-3.5" aria-hidden="true" />
            New engagement
          </Link>
        )}
      </div>

      {/* The list component is a client component that fetches and renders
          the engagement list with loading / error / empty states. */}
      <EngagementList slice={slice} unit={unit} agent={agent} />
    </div>
  );
}
