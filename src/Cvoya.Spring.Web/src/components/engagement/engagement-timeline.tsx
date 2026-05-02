"use client";

// Engagement Timeline.
//
// Thin wrapper around the shared <ConversationView> primitive (#1554).
// The engagement portal uses the activity-link footer affordance per row
// (the engagement portal's "View in activity →" pattern is the long-
// standing design); the inbox uses the metadata-toggle variant. See
// `components/conversation/conversation-view.tsx` for the shared
// implementation.

import { ConversationView } from "@/components/conversation/conversation-view";

interface EngagementTimelineProps {
  threadId: string;
}

export function EngagementTimeline({ threadId }: EngagementTimelineProps) {
  return (
    <ConversationView
      threadId={threadId}
      rowActions="activity-link"
      testId="engagement-timeline"
      eventListTestId="engagement-timeline-events"
      renderEmpty={({ filter, totalEvents }) => (
        <p className="text-sm text-muted-foreground">
          {totalEvents === 0
            ? "No events in this engagement yet."
            : filter === "messages"
              ? "No messages yet — switch to “Full timeline” to see all events."
              : "No events match the current filter."}
        </p>
      )}
    />
  );
}
