// Engagement detail view (E2.5 + E2.6, #1417, #1418).
//
// URL: /engagement/<id>
//
// Renders the full engagement detail:
//   - Timeline: streamed via SSE on /api/v1/tenant/activity/stream?thread=<id>
//   - Composer: visible when the current human is a participant (kind=information)
//   - Observe banner: visible when the human is NOT a participant (A2A / other)
//   - "Answer this question" CTA: visible when there is a pending inbox question
//     for this engagement (kind=answer on submit)
//
// The page shell (header + back link) is server-rendered; the interactive
// detail (Timeline + composer) is a client component.

import { MessagesSquare } from "lucide-react";
import Link from "next/link";
import type { Metadata } from "next";

import { EngagementDetail } from "@/components/engagement/engagement-detail";

interface EngagementDetailPageProps {
  params: Promise<{ id: string }>;
}

export async function generateMetadata({
  params,
}: EngagementDetailPageProps): Promise<Metadata> {
  const { id } = await params;
  return {
    title: `Engagement ${id} — Spring Voyage`,
  };
}

export default async function EngagementDetailPage({
  params,
}: EngagementDetailPageProps) {
  const { id } = await params;

  return (
    <div className="flex flex-col h-full" data-testid="engagement-detail-page">
      {/* Page header */}
      <div className="flex items-center gap-2 pb-4 border-b border-border">
        <Link
          href="/engagement/mine"
          className="text-xs text-muted-foreground hover:text-foreground transition-colors focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring rounded"
          aria-label="Back to my engagements"
        >
          ← My engagements
        </Link>
        <span className="text-muted-foreground" aria-hidden="true">/</span>
        <h1 className="flex items-center gap-2 text-base font-semibold min-w-0">
          <MessagesSquare className="h-4 w-4 shrink-0" aria-hidden="true" />
          <span className="font-mono text-sm text-muted-foreground truncate">
            {id}
          </span>
        </h1>
      </div>

      {/* Client-side detail: Timeline + composer + observe banner + CTA */}
      <div className="flex-1 min-h-0 -mx-4 md:-mx-6">
        <EngagementDetail threadId={id} />
      </div>
    </div>
  );
}
