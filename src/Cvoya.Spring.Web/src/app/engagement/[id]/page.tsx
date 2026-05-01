// Engagement detail view.
//
// URL: /engagement/<id>
//
// Renders the full engagement detail:
//   - Timeline: streamed via SSE, with a Messages/Full-timeline filter.
//   - Composer: visible when the current human is a participant.
//   - Observe banner: visible when the human is NOT a participant.
//   - "Answer this question" CTA: visible when there is a pending inbox
//     question for this engagement.
//
// The shell (sidebar list, header, "+ New engagement" CTA) is supplied
// by EngagementShell; this page is just the detail surface.

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
    <div
      className="flex h-full min-h-0 flex-1 flex-col"
      data-testid="engagement-detail-page"
    >
      <EngagementDetail threadId={id} />
    </div>
  );
}
