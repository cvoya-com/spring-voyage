"use client";

// Engagement detail view — client component (E2.5 + E2.6, #1417, #1418).
//
// Three logical regions (per the E2.5 spec):
//   1. Timeline — full per-thread Timeline, streamed via SSE.
//   2. Send-message composer — visible only when the current human is
//      a participant. Posts `kind: "information"` by default.
//   3. Observe banner — visible when the human is NOT a participant;
//      read-only Timeline with a clear "You are observing" cue.
//
// E2.6 additions:
//   - "Answer this question" call-to-action: shown above the composer
//     when the engagement's most-recent non-human message event appears
//     to be a question (detected from eventType or inbox status).
//   - Answering focuses the composer in "answer" mode; submitted with
//     `kind: "answer"`.
//
// This component is "use client" because it drives live SSE streaming,
// TanStack Query hooks, and interactive composer state.

import { useState, useMemo, useCallback } from "react";
import { Eye, MessageCircleQuestion } from "lucide-react";
import { ApiErrorMessage } from "@/components/ui/api-error-message";
import { Badge } from "@/components/ui/badge";
import { Skeleton } from "@/components/ui/skeleton";
import { Button } from "@/components/ui/button";
import { RuntimeStatusBadge } from "@/components/runtime-status-badge";
import {
  useThread,
  useCurrentUser,
  useInbox,
  useCallerHumans,
} from "@/lib/api/queries";
import {
  addressOf,
  idOf,
  participantDisplayName,
  runtimeKindOf,
  type AddressLike,
} from "@/components/thread/role";
import { EngagementTimeline } from "./engagement-timeline";
import { EngagementComposer } from "./engagement-composer";

interface EngagementDetailProps {
  threadId: string;
}

// ---------------------------------------------------------------------------
// Observe-only banner (E2.5)
// ---------------------------------------------------------------------------

function ObserveBanner() {
  return (
    <div
      role="status"
      aria-live="polite"
      className="mx-4 mt-4 flex items-start gap-2 rounded-md border border-primary/40 bg-primary/10 px-3 py-2 text-sm"
      data-testid="engagement-observe-banner"
    >
      <Eye className="mt-0.5 h-4 w-4 shrink-0 text-primary" aria-hidden="true" />
      <span className="text-foreground">
        You are observing this engagement — not a participant. The Timeline is
        read-only; messages cannot be sent from here.{" "}
        <span className="text-muted-foreground">
          (Joining running engagements is deferred to v0.2 — see{" "}
          <a
            href="https://github.com/cvoya-com/spring-voyage/issues/1292"
            target="_blank"
            rel="noreferrer"
            className="underline underline-offset-2 hover:text-foreground"
          >
            #1292
          </a>
          .)
        </span>
      </span>
    </div>
  );
}

// ---------------------------------------------------------------------------
// "Answer this question" call-to-action (E2.6)
// ---------------------------------------------------------------------------

interface QuestionCtaProps {
  onAnswer: () => void;
}

function QuestionCta({ onAnswer }: QuestionCtaProps) {
  return (
    <div
      role="alert"
      className="mx-4 mt-4 flex items-start gap-3 rounded-md border border-warning/50 bg-warning/10 px-3 py-2"
      data-testid="engagement-question-cta"
    >
      <MessageCircleQuestion
        className="mt-0.5 h-4 w-4 shrink-0 text-warning"
        aria-hidden="true"
      />
      <div className="flex flex-1 items-center justify-between gap-2">
        <div>
          <p className="text-sm font-medium text-foreground">
            A unit or agent is asking you a question.
          </p>
          <p className="text-xs text-muted-foreground mt-0.5">
            Answer below to unblock the engagement.
          </p>
        </div>
        <Button
          size="sm"
          variant="outline"
          onClick={onAnswer}
          data-testid="engagement-answer-button"
          className="shrink-0 border-warning/60 text-warning hover:bg-warning/10"
        >
          Answer this question
        </Button>
      </div>
    </div>
  );
}

// ---------------------------------------------------------------------------
// Loading state
// ---------------------------------------------------------------------------

function EngagementDetailLoading() {
  return (
    <div
      className="p-4 space-y-3"
      role="status"
      aria-live="polite"
      data-testid="engagement-detail-loading"
    >
      <Skeleton className="h-6 w-48" />
      <Skeleton className="h-4 w-full" />
      <Skeleton className="h-28 w-full" />
      <Skeleton className="h-28 w-3/4" />
    </div>
  );
}

// ---------------------------------------------------------------------------
// Main detail component
// ---------------------------------------------------------------------------

export function EngagementDetail({ threadId }: EngagementDetailProps) {
  const threadQuery = useThread(threadId);
  const userQuery = useCurrentUser();
  const callerHumansQuery = useCallerHumans();
  const inboxQuery = useInbox({ staleTime: 10_000 });

  // Composer mode: "information" (default) or "answer" (triggered by CTA).
  const [composerKind, setComposerKind] = useState<"information" | "answer">(
    "information",
  );

  const thread = threadQuery.data;
  const participants = useMemo(
    () => thread?.summary?.participants ?? [],
    [thread?.summary?.participants],
  );

  // The set of Hat ids that resolve to "me" on this thread.
  //
  // #2888: an operator wears one Hat per unit (ADR-0062), so the Hat
  // stamped on a unit-scoped thread — carried as the participant `id`,
  // picked per-thread by TenantUserHumanResolver — differs from the
  // auth-username Hat that `/auth/me` resolves. Gating "am I a
  // participant?" on the lone `/me.id` (the pre-#2888 code) misclassified
  // the operator as an observer on their own threads: observe banner, no
  // composer, even where they were the literal sender. Gate on the
  // caller's full bound-Hat set instead (`useCallerHumans()`, the same
  // set powering the composer's "As…" selector). The `/auth/me` id is
  // folded in as a floor so the check never narrows below the previous
  // behaviour even if a bound Hat is momentarily absent from the set.
  //
  // #2082: identity is a typed Guid concept — compare on each
  // participant's stable `id`, never on the display address (which has
  // legitimately drifted between `scheme:<hex>` and `scheme:id:<hex>`).
  const myHatIds = useMemo(() => {
    const ids = new Set<string>();
    const meId = userQuery.data?.id?.trim().toLowerCase();
    if (meId) ids.add(meId);
    for (const hat of callerHumansQuery.data ?? []) {
      const hatId = hat.humanId?.trim().toLowerCase();
      if (hatId) ids.add(hatId);
    }
    return ids;
  }, [userQuery.data?.id, callerHumansQuery.data]);

  const isMine = useCallback(
    (p: AddressLike) => {
      const id = idOf(p);
      return id !== null && myHatIds.has(id);
    },
    [myHatIds],
  );

  const isParticipant = useMemo(
    () => participants.some(isMine),
    [participants, isMine],
  );

  // Detect whether there's a pending question for this engagement in the inbox.
  // The inbox items carry `threadId` so we can match.
  const hasPendingQuestion = useMemo(() => {
    const inbox = inboxQuery.data ?? [];
    return inbox.some((item) => item.threadId === threadId);
  }, [inboxQuery.data, threadId]);

  if (
    threadQuery.isPending ||
    userQuery.isPending ||
    callerHumansQuery.isPending
  ) {
    return <EngagementDetailLoading />;
  }

  if (threadQuery.error) {
    return (
      <div className="m-4" data-testid="engagement-detail-error">
        <ApiErrorMessage error={threadQuery.error} />
      </div>
    );
  }

  if (!thread) {
    return (
      <p
        className="m-4 text-sm text-muted-foreground"
        data-testid="engagement-detail-not-found"
      >
        Engagement not found.
      </p>
    );
  }

  // Header label: display names of everyone except the operator.
  // For observers (none of the caller's Hats are participants) we show
  // every name so the header is meaningful. Names that fail to resolve
  // to anything human-readable are dropped quietly rather than leaked as
  // raw GUIDs — the previous fallback emitted strings like
  // "agent:id:<uuid>", which is the bug tracked in #1630. #2888: exclude
  // every Hat that resolves to the operator (not just `/me.id`) so the
  // operator's own unit-scoped sending Hat is not listed as a separate
  // participant in the header.
  const otherParticipants = participants.filter((p) => !isMine(p));
  const fallbackHeader =
    otherParticipants.length === 0
      ? "Just you"
      : otherParticipants.length === 1
        ? "Unknown participant"
        : "Unknown participants";
  const renderableHeaderEntries = otherParticipants
    .map((p) => ({ p, name: participantDisplayName(p) }))
    .filter((e) => Boolean(e.name));

  return (
    <div
      className="flex flex-col min-h-0 flex-1"
      data-testid="engagement-detail"
    >
      {/* Participant summary header.
          #2100: each non-human participant carries a small runtime-status
          chip so silent moments (head-of-line victims of another thread)
          don't read as "the agent is broken". Humans don't get a chip —
          their absence is the indicator. */}
      <div className="flex items-center gap-2 border-b border-border px-4 py-2 text-xs">
        <div
          className="flex min-w-0 flex-wrap items-center gap-x-3 gap-y-1 font-medium text-foreground"
          data-testid="engagement-detail-header-names"
        >
          {renderableHeaderEntries.length === 0 ? (
            <span className="truncate">{fallbackHeader}</span>
          ) : (
            renderableHeaderEntries.map(({ p, name }, idx) => {
              const kind = runtimeKindOf(p);
              const actorId = idOf(p);
              return (
                <span
                  key={`${addressOf(p)}-${idx}`}
                  className="inline-flex min-w-0 items-center gap-1.5"
                >
                  <span className="truncate">{name}</span>
                  {kind && (
                    <RuntimeStatusBadge
                      kind={kind}
                      id={actorId}
                      size="dot"
                      testId={`engagement-detail-header-status-${actorId ?? "unknown"}`}
                    />
                  )}
                </span>
              );
            })
          )}
        </div>
        {!isParticipant && (
          <Badge variant="secondary" className="ml-auto shrink-0">
            Observer
          </Badge>
        )}
      </div>

      {/* Observe-only banner (rendered above the timeline, not blocking it) */}
      {!isParticipant && <ObserveBanner />}

      {/* E2.6 "Answer this question" call-to-action — only for participants
          with a pending inbox question on this thread */}
      {isParticipant && hasPendingQuestion && composerKind !== "answer" && (
        <QuestionCta
          onAnswer={() => setComposerKind("answer")}
        />
      )}

      {/* Timeline — always visible (read-only for observers).
          The wrapper is a flex column so ConversationView's `flex-1`
          outer div grows to fill the available height. Without that,
          the inner `overflow-y-auto` events list has no constrained
          height and the timeline does not scroll (#1574 follow-up).

          Layout fork (#1630): observers see a left-justified timeline
          with non-message events as click-to-expand cards; participants
          keep the chat-style dialog. */}
      <div className="flex flex-1 min-h-0 flex-col overflow-hidden">
        <EngagementTimeline
          threadId={threadId}
          layout={isParticipant ? "dialog" : "timeline"}
        />
      </div>

      {/* Composer — only for participants */}
      {isParticipant && (
        <EngagementComposer
          threadId={threadId}
          participants={participants.map((p) => addressOf(p))}
          initialKind={composerKind}
          onKindChange={setComposerKind}
          onSendSuccess={() => setComposerKind("information")}
        />
      )}
    </div>
  );
}
