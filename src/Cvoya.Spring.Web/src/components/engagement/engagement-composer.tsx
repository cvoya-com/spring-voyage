"use client";

// Engagement message composer (E2.5 / E2.6, #1417, #1418).
//
// A focused composer for sending messages into an engagement thread.
// Supports two modes:
//   - "information" (default): a regular status update or message.
//   - "answer": answering a clarifying question from a unit/agent.
//     This mode is activated when the caller sets `initialKind="answer"`,
//     which happens when the user clicks "Answer this question" CTA.
//
// The composer is only visible when the current human IS a participant
// in the engagement. The parent page enforces this via the `isParticipant`
// prop — when false, the composer is not rendered.
//
// Layout (#1552): a compact two-line textarea with the Send button pinned
// to its right, both sharing the same row. The recipient is implicit —
// the user is already inside the engagement — so the composer derives the
// default recipient from the participant list (first non-human) and does
// not surface a recipient picker. The keyboard shortcut hint lives on the
// Send button's tooltip rather than as inline body text.
//
// CLI parity:
//   - Information: spring engagement send <id> <address> <message>
//   - Answer:      spring engagement answer <id> <address> <message>

import { useState, useRef, useEffect } from "react";
import { useMutation, useQueryClient } from "@tanstack/react-query";
import { Send, MessageCircleQuestion } from "lucide-react";

import { Button } from "@/components/ui/button";
import { Badge } from "@/components/ui/badge";
import { useToast } from "@/components/ui/toast";
import { api } from "@/lib/api/client";
import { queryKeys } from "@/lib/api/query-keys";
import { parseThreadSource } from "@/components/thread/role";

type MessageKind = "information" | "answer";

interface EngagementComposerProps {
  threadId: string;
  /** Participants on the thread — used to pre-populate the recipient picker. */
  participants?: string[];
  /**
   * Controlled `kind` for the composer. Parent owns the state.
   * When "answer", visual cues indicate a reply and the textarea is focused
   * on prop change. The user can dismiss back to "information" via the
   * inline "Send as regular message instead" button (which calls onKindChange).
   */
  initialKind?: MessageKind;
  /** Called when the composer toggles its mode internally. */
  onKindChange?: (next: MessageKind) => void;
  /**
   * Called after a successful send so the parent can clear any
   * "question pending" state and reset its own kind back to "information".
   */
  onSendSuccess?: () => void;
}

/**
 * Composer for sending messages into an engagement.
 *
 * Sending a message routes to `POST /api/v1/tenant/threads/{id}/messages`
 * with the appropriate `kind` field.
 *
 * The default recipient is the first non-human participant. The user can
 * change the recipient via the quick-pick pills.
 */
export function EngagementComposer({
  threadId,
  participants = [],
  initialKind = "information",
  onKindChange,
  onSendSuccess,
}: EngagementComposerProps) {
  const { toast } = useToast();
  const queryClient = useQueryClient();
  const textareaRef = useRef<HTMLTextAreaElement>(null);

  // The recipient is implicit — the user is already inside the engagement
  // — so we derive it from the participant list (#1552). Default to the
  // first non-human participant; fall back to the first participant when
  // every party is human (rare). The recipient is recomputed when the
  // participants prop changes so an engagement that is loaded after mount
  // does not get stuck with an empty recipient.
  const recipient = (() => {
    for (const p of participants) {
      if (!p.startsWith("human://")) return p;
    }
    return participants[0] ?? "";
  })();

  const [text, setText] = useState("");

  // `kind` is fully controlled by the parent. Toggling the inline
  // "Send as regular message instead" button calls onKindChange("information");
  // post-send reset is the parent's responsibility (see onSendSuccess).
  const kind = initialKind;
  const setKind = (next: MessageKind) => onKindChange?.(next);

  // Focus the textarea when the parent flips into answer mode.
  useEffect(() => {
    if (initialKind === "answer") {
      textareaRef.current?.focus();
    }
  }, [initialKind]);

  const send = useMutation({
    mutationFn: async () => {
      const trimmed = text.trim();
      const target = recipient.trim();
      if (!trimmed) throw new Error("Message text is required.");
      if (!target) {
        throw new Error(
          "No recipient available — the engagement has no non-human participant.",
        );
      }

      const { scheme, path } = parseThreadSource(target);
      if (!scheme || !path) {
        throw new Error(
          "Recipient must be in scheme://path form (e.g. agent://ada).",
        );
      }

      return api.sendThreadMessage(threadId, {
        to: { scheme, path },
        text: trimmed,
        kind,
      });
    },
    onSuccess: () => {
      setText("");
      // Mode reset after a successful send is the parent's job — invoked via
      // onSendSuccess below. Don't toggle local kind here (it's controlled).
      queryClient.invalidateQueries({
        queryKey: queryKeys.threads.detail(threadId),
      });
      queryClient.invalidateQueries({ queryKey: queryKeys.threads.all });
      queryClient.invalidateQueries({ queryKey: queryKeys.threads.inbox() });
      queryClient.invalidateQueries({ queryKey: queryKeys.activity.all });
      onSendSuccess?.();
    },
    onError: (err) => {
      toast({
        title: "Failed to send message",
        description: err instanceof Error ? err.message : String(err),
        variant: "destructive",
      });
    },
  });

  const submit = () => {
    if (send.isPending) return;
    send.mutate();
  };

  const handleSubmit = (e: React.FormEvent) => {
    e.preventDefault();
    submit();
  };

  const handleKeyDown = (e: React.KeyboardEvent<HTMLTextAreaElement>) => {
    if ((e.metaKey || e.ctrlKey) && e.key === "Enter") {
      e.preventDefault();
      submit();
    }
  };

  const isAnswerMode = kind === "answer";

  const sendTooltip = send.isPending ? "Sending…" : "⌘/Ctrl+Enter to send";

  return (
    // shrink-0 keeps the composer at its intrinsic height inside the
    // engagement-detail flex column so the timeline above it owns the only
    // scrollbar (#1552). Without it, an unusually tall textarea or banner
    // could compete with the timeline and break scroll.
    <form
      onSubmit={handleSubmit}
      className={[
        "shrink-0 space-y-2 border-t bg-muted/20 p-3",
        isAnswerMode
          ? "border-warning/40 bg-warning/5"
          : "border-border",
      ].join(" ")}
      aria-label={isAnswerMode ? "Answer clarifying question" : "Send message"}
      data-testid="engagement-composer"
      data-kind={kind}
    >
      {/* Answer-mode banner — kept because it is the only signal that the
          composer is now in answer mode and provides the escape hatch. */}
      {isAnswerMode && (
        <div className="flex items-center gap-2 text-sm">
          <MessageCircleQuestion
            className="h-4 w-4 text-warning shrink-0"
            aria-hidden="true"
          />
          <span className="text-warning font-medium">Answering a question</span>
          <Badge variant="warning" className="text-[10px] px-1.5 h-5">
            answer
          </Badge>
          <button
            type="button"
            onClick={() => setKind("information")}
            className="ml-auto text-xs text-muted-foreground underline underline-offset-2 hover:text-foreground focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring rounded"
            aria-label="Switch to regular message mode"
          >
            Send as regular message instead
          </button>
        </div>
      )}

      {/* Single-row composer: 2-line textarea on the left, full-height
          Send button on the right. items-stretch makes the button span
          the textarea's height so the row reads as one unit (#1552). */}
      <div className="flex items-stretch gap-2">
        <textarea
          ref={textareaRef}
          value={text}
          onChange={(e) => setText(e.target.value)}
          onKeyDown={handleKeyDown}
          placeholder={isAnswerMode ? "Type your answer…" : "Type a message…"}
          rows={2}
          className="min-w-0 flex-1 resize-none rounded-md border border-input bg-background px-3 py-2 text-sm shadow-sm focus-visible:outline-none focus-visible:ring-1 focus-visible:ring-ring"
          aria-label={isAnswerMode ? "Your answer" : "Message text"}
        />
        <Button
          type="submit"
          disabled={send.isPending || !text.trim() || !recipient.trim()}
          title={sendTooltip}
          aria-label={
            isAnswerMode
              ? "Send answer (⌘/Ctrl+Enter)"
              : "Send message (⌘/Ctrl+Enter)"
          }
          className={[
            "h-auto shrink-0 self-stretch px-4",
            isAnswerMode
              ? "bg-warning hover:bg-warning/90 text-warning-foreground"
              : "",
          ].join(" ")}
        >
          <Send className="mr-1 h-4 w-4" aria-hidden="true" />
          {isAnswerMode ? "Send answer" : "Send"}
        </Button>
      </div>
    </form>
  );
}
