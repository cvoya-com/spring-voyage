// Engagement detail view — placeholder (E2.3, #1415).
//
// URL: /engagement/<id>   (where <id> is the engagement / thread id)
//
// Empty placeholder for the engagement detail view (E2.5, #1417 will fill).
// E2.5 will render the full Timeline, send-message composer, and inbound
// clarification UX here.

import { MessagesSquare } from "lucide-react";
import { Card, CardContent } from "@/components/ui/card";
import Link from "next/link";

interface EngagementDetailPageProps {
  params: Promise<{ id: string }>;
}

export default async function EngagementDetailPage({
  params,
}: EngagementDetailPageProps) {
  const { id } = await params;

  return (
    <div className="space-y-6" data-testid="engagement-detail-page">
      <div>
        <h1 className="flex items-center gap-2 text-2xl font-bold">
          <MessagesSquare className="h-5 w-5" aria-hidden="true" />
          Engagement
          <span
            className="font-mono text-lg text-muted-foreground"
            data-testid="engagement-detail-id"
          >
            {id}
          </span>
        </h1>
      </div>

      <Card data-testid="engagement-detail-placeholder">
        <CardContent className="flex flex-col items-center justify-center p-8 text-center">
          <MessagesSquare
            className="mb-3 h-10 w-10 text-muted-foreground"
            aria-hidden="true"
          />
          <p className="mb-1 font-medium">
            Engagement {id} — detail view coming soon
          </p>
          <p className="text-sm text-muted-foreground">
            The full Timeline, send-message composer, and clarification UX land
            in E2.5 (#1417).
          </p>
        </CardContent>
      </Card>

      <p className="text-xs text-muted-foreground">
        <Link href="/engagement/mine" className="text-primary hover:underline">
          Back to my engagements
        </Link>
      </p>
    </div>
  );
}
