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
   * When set to "answer", the composer opens in "answer-a-question" mode:
   * the kind is locked to "answer" and visual cues indicate it's a reply.
   * The user can dismiss back to "information" mode.
   */
  initialKind?: MessageKind;
  /**
   * Called after a successful send so the parent can clear any
   * "question pending" state.
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
  onSendSuccess,
}: EngagementComposerProps) {
  const { toast } = useToast();
  const queryClient = useQueryClient();
  const textareaRef = useRef<HTMLTextAreaElement>(null);

  // Determine the default recipient: the first non-human participant.
  const defaultRecipient = (() => {
    for (const p of participants) {
      if (!p.startsWith("human://")) return p;
    }
    return participants[0] ?? "";
  })();

  const [recipient, setRecipient] = useState(defaultRecipient);
  const [text, setText] = useState("");
  const [kind, setKind] = useState<MessageKind>(initialKind);

  // When initialKind changes (e.g. user clicks "Answer this question"),
  // switch mode and focus the textarea.
  useEffect(() => {
    setKind(initialKind);
    if (initialKind === "answer") {
      textareaRef.current?.focus();
    }
  }, [initialKind]);

  const send = useMutation({
    mutationFn: async () => {
      const trimmed = text.trim();
      const target = recipient.trim();
      if (!trimmed) throw new Error("Message text is required.");
      if (!target) throw new Error("Recipient address is required.");

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
      // Reset to information mode after a successful answer.
      setKind("information");
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

  return (
    <form
      onSubmit={handleSubmit}
      className={[
        "space-y-2 border-t bg-muted/20 p-4",
        isAnswerMode
          ? "border-warning/40 bg-warning/5"
          : "border-border",
      ].join(" ")}
      aria-label={isAnswerMode ? "Answer clarifying question" : "Send message"}
      data-testid="engagement-composer"
      data-kind={kind}
    >
      {/* Answer-mode banner */}
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

      {/* Recipient quick-pick pills */}
      {participants.length > 0 && (
        <div className="flex flex-wrap items-center gap-1 text-xs">
          <span className="text-muted-foreground">To:</span>
          {participants
            .filter((p) => !p.startsWith("human://"))
            .map((p) => (
              <button
                key={p}
                type="button"
                onClick={() => setRecipient(p)}
                className={
                  recipient === p
                    ? "rounded border border-primary bg-primary/10 px-2 py-0.5 font-mono text-[11px]"
                    : "rounded border border-input bg-background px-2 py-0.5 font-mono text-[11px] hover:bg-muted"
                }
                aria-pressed={recipient === p}
              >
                {p}
              </button>
            ))}
        </div>
      )}

      {/* Recipient address input */}
      <input
        type="text"
        value={recipient}
        onChange={(e) => setRecipient(e.target.value)}
        placeholder="Recipient (scheme://path, e.g. agent://ada)"
        className="flex h-9 w-full rounded-md border border-input bg-background px-3 py-1 font-mono text-xs shadow-sm focus-visible:outline-none focus-visible:ring-1 focus-visible:ring-ring"
        aria-label="Recipient address"
      />

      {/* Message textarea */}
      <textarea
        ref={textareaRef}
        value={text}
        onChange={(e) => setText(e.target.value)}
        onKeyDown={handleKeyDown}
        placeholder={
          isAnswerMode
            ? "Type your answer… (⌘/Ctrl+Enter to send)"
            : "Type a message… (⌘/Ctrl+Enter to send)"
        }
        rows={3}
        className="flex min-h-[60px] w-full resize-y rounded-md border border-input bg-background px-3 py-2 text-sm shadow-sm focus-visible:outline-none focus-visible:ring-1 focus-visible:ring-ring"
        aria-label={isAnswerMode ? "Your answer" : "Message text"}
      />

      <div className="flex items-center justify-end gap-2">
        <span className="text-xs text-muted-foreground">
          {send.isPending ? "Sending…" : "⌘/Ctrl+Enter to send"}
        </span>
        <Button
          type="submit"
          size="sm"
          disabled={send.isPending || !text.trim() || !recipient.trim()}
          className={
            isAnswerMode ? "bg-warning hover:bg-warning/90 text-warning-foreground" : ""
          }
        >
          <Send className="mr-1 h-4 w-4" aria-hidden="true" />
          {isAnswerMode ? "Send answer" : "Send"}
        </Button>
      </div>
    </form>
  );
}
