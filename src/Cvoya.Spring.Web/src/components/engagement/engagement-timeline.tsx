"use client";

// Engagement Timeline.
//
// Thin wrapper around the shared <ConversationView> primitive (#1554).
// The engagement portal uses the activity-link footer affordance per row
// (the engagement portal's "View in activity →" pattern is the long-
// standing design); the inbox uses the metadata-toggle variant. See
// `components/conversation/conversation-view.tsx` for the shared
// implementation.
//
// #1630: when the active user is observing (not a participant) the
// dialog metaphor (bubbles aligned left/right by sender) is wrong —
// there is no "self" axis to mirror against. The detail component
// passes `layout="timeline"` for that case; rows render left-justified
// and non-message events fold into compact <ThreadEventCard>s.

import { ConversationView } from "@/components/conversation/conversation-view";
import type { ConversationLayout } from "@/components/conversation/conversation-view";

interface EngagementTimelineProps {
  threadId: string;
  /**
   * Visual layout. `"dialog"` (default) for participant view; `"timeline"`
   * for observer view (#1630). See {@link ConversationLayout}.
   */
  layout?: ConversationLayout;
}

export function EngagementTimeline({
  threadId,
  layout = "dialog",
}: EngagementTimelineProps) {
  return (
    <ConversationView
      threadId={threadId}
      rowActions="activity-link"
      layout={layout}
      defaultFilter={layout === "timeline" ? "full" : "messages"}
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
