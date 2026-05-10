"use client";

// Engagement message composer (E2.5 / E2.6, #1417, #1418).
//
// Thin wrapper around the shared <MessageComposer> primitive (#1554)
// that derives the implicit recipient from the engagement's participant
// list. The compact textarea + side-by-side Send button layout, the
// keyboard-shortcut tooltip, and the answer-mode banner are all owned
// by the shared composer; this file is just glue.
//
// CLI parity:
//   - Information: spring engagement send <id> <address> <message>
//   - Answer:      spring engagement answer <id> <address> <message>

import { useMemo } from "react";

import {
  MessageComposer,
  type MessageKind,
  type MessageRecipient,
} from "@/components/conversation/message-composer";
import { parseThreadSource } from "@/components/thread/role";

interface EngagementComposerProps {
  threadId: string;
  /**
   * Participant addresses on the thread. The recipient is implicit —
   * we pick the first non-human participant so the user is not asked
   * to re-state who they are messaging from inside the engagement.
   */
  participants?: string[];
  /**
   * Controlled `kind` for the composer. Parent owns the state.
   * When "answer", the shared composer focuses the textarea and renders
   * the "Answering a question" banner with an escape hatch.
   */
  initialKind?: MessageKind;
  /** Called when the composer toggles its mode internally. */
  onKindChange?: (next: MessageKind) => void;
  /**
   * Called after a successful send so the parent can reset its own
   * state — typically clearing answer mode back to "information".
   */
  onSendSuccess?: () => void;
}

function deriveRecipient(participants: string[]): MessageRecipient | null {
  // #2082 follow-up: after RenderAddress was unified to the canonical
  // `scheme:<hex>` form, raw `startsWith("human://")` no longer matched
  // the new human address shape, so the human slipped through and was
  // picked as the recipient — meaning the user's message was routed
  // back to themselves via HumanActor.ReceiveAsync instead of reaching
  // the unit. Parse the scheme through `parseThreadSource` and skip
  // anything human-shaped, regardless of which historical form the
  // address arrives in.
  for (const p of participants) {
    const parsed = parseThreadSource(p);
    if (parsed.scheme !== "human" && parsed.scheme && parsed.path) {
      return { scheme: parsed.scheme, path: parsed.path };
    }
  }
  if (participants.length > 0) {
    const { scheme, path } = parseThreadSource(participants[0]);
    if (scheme && path) return { scheme, path };
  }
  return null;
}

export function EngagementComposer({
  threadId,
  participants = [],
  initialKind = "information",
  onKindChange,
  onSendSuccess,
}: EngagementComposerProps) {
  const recipient = useMemo(
    () => deriveRecipient(participants),
    [participants],
  );

  return (
    <MessageComposer
      threadId={threadId}
      recipient={recipient}
      kind={initialKind}
      onKindChange={onKindChange}
      onSendSuccess={onSendSuccess}
      testId="engagement-composer"
      recipientMissingMessage="No recipient available — the engagement has no non-human participant."
    />
  );
}
