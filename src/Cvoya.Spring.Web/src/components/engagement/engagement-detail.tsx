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

import { useState, useMemo } from "react";
import { Eye, MessageCircleQuestion } from "lucide-react";
import { Badge } from "@/components/ui/badge";
import { Skeleton } from "@/components/ui/skeleton";
import { Button } from "@/components/ui/button";
import { useThread, useCurrentUser, useInbox } from "@/lib/api/queries";
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

  // Determine whether the current authenticated human is a participant.
  // The user profile returns an `address` field (human:// scheme://path).
  const currentUserAddress = userQuery.data?.address;
  const isParticipant = useMemo(() => {
    if (!currentUserAddress) return false;
    return participants.some((p) => p.address === currentUserAddress);
  }, [participants, currentUserAddress]);

  // Detect whether there's a pending question for this engagement in the inbox.
  // The inbox items carry `threadId` so we can match.
  const hasPendingQuestion = useMemo(() => {
    const inbox = inboxQuery.data ?? [];
    return inbox.some((item) => item.threadId === threadId);
  }, [inboxQuery.data, threadId]);

  if (threadQuery.isPending || userQuery.isPending) {
    return <EngagementDetailLoading />;
  }

  if (threadQuery.error) {
    return (
      <div
        role="alert"
        className="m-4 rounded-md border border-destructive/50 bg-destructive/10 px-3 py-2 text-sm text-destructive"
        data-testid="engagement-detail-error"
      >
        Could not load engagement:{" "}
        {threadQuery.error instanceof Error
          ? threadQuery.error.message
          : String(threadQuery.error)}
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

  // Header label: display names of everyone except the current user.
  // Falls back to all participants when self is unknown so the header is
  // never blank.
  const headerNames = (() => {
    const others = currentUserAddress
      ? participants.filter((p) => p.address !== currentUserAddress)
      : participants;
    if (others.length === 0) return "Just you";
    return others.map((p) => p.displayName).join(" · ");
  })();

  return (
    <div
      className="flex flex-col min-h-0 flex-1"
      data-testid="engagement-detail"
    >
      {/* Participant summary header */}
      <div className="flex items-center gap-2 border-b border-border px-4 py-2 text-xs">
        <span
          className="truncate font-medium text-foreground"
          data-testid="engagement-detail-header-names"
        >
          {headerNames}
        </span>
        {thread.summary?.status && (
          <Badge variant="outline">{thread.summary.status}</Badge>
        )}
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

      {/* Timeline — always visible (read-only for observers) */}
      <div className="flex-1 min-h-0 overflow-hidden">
        <EngagementTimeline threadId={threadId} />
      </div>

      {/* Composer — only for participants */}
      {isParticipant && (
        <EngagementComposer
          threadId={threadId}
          participants={participants.map((p) => p.address)}
          initialKind={composerKind}
          onKindChange={setComposerKind}
          onSendSuccess={() => setComposerKind("information")}
        />
      )}
    </div>
  );
}
