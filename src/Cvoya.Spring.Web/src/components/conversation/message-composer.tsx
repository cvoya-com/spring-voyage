"use client";

// Shared message composer (#1554).
//
// Compact composer used by the engagement detail surface, the unit/agent
// "Messages" tab, and the inbox right-pane. The visual composition was
// settled in PR #1553 (single-row textarea + full-height Send button on
// the right, with the keyboard-shortcut hint as a tooltip on the Send
// button so the body text stays clean) — this component lifts that into
// a reusable primitive so every conversation surface speaks the same
// affordance instead of carrying its own near-copy.
//
// Behaviour:
//   - Submits via Cmd/Ctrl+Enter or the Send button.
//   - When `threadId` is set, posts to `POST /threads/{id}/messages`.
//     When null (used by the unit/agent Messages tab when no 1:1 thread
//     exists yet), posts to `POST /messages` so the server allocates a
//     fresh thread id; the next refetch surfaces the new thread.
//   - Optimistically injects the just-sent message into the thread
//     detail cache so the user sees their own bubble immediately rather
//     than waiting on the SSE round-trip.
//   - Optional answer-mode (engagement E2.6): swaps the heading to
//     "Answering a question", routes the request as `kind: "answer"`,
//     and exposes a "Send as regular message instead" escape hatch.
//
// The Send button always carries `title="⌘/Ctrl+Enter to send"` (browser
// tooltip on hover) and bakes the same hint into its `aria-label` so
// screen-reader users discover it too. The hint is no longer rendered as
// inline body text — the row stays a single line.

import { useEffect, useRef, useState } from "react";
import { useMutation, useQueryClient } from "@tanstack/react-query";
import { Loader2, MessageCircleQuestion, Send } from "lucide-react";

import { Badge } from "@/components/ui/badge";
import { Button } from "@/components/ui/button";
import { useToast } from "@/components/ui/toast";
import { api } from "@/lib/api/client";
import { queryKeys } from "@/lib/api/query-keys";
import type { ThreadDetail } from "@/lib/api/types";

export type MessageKind = "information" | "answer";

export interface MessageRecipient {
  scheme: string;
  path: string;
}

export interface MessageComposerProps {
  /**
   * Existing thread to append to. When `null`, the composer creates a
   * new thread on first send via `POST /messages` and the server's
   * auto-generated thread id is picked up by the next refetch.
   */
  threadId: string | null;
  /**
   * Resolved recipient. When `null`, the composer renders disabled with
   * a hint that no recipient is available. Consumers derive this from
   * thread participants (engagement, inbox) or from a tab's hosting
   * unit/agent (unit/agent Messages tab).
   */
  recipient: MessageRecipient | null;
  /** Controlled message kind. Defaults to "information". */
  kind?: MessageKind;
  /**
   * Called when the composer flips kind internally — currently only via
   * the "Send as regular message instead" escape hatch in answer mode.
   */
  onKindChange?: (next: MessageKind) => void;
  /** Called after a successful send so the parent can reset its own state. */
  onSendSuccess?: () => void;
  /** Optional placeholder override. */
  placeholder?: string;
  /** Test-id for the outer form. */
  testId?: string;
  /**
   * Override for the recipient-missing copy. Defaults to a generic
   * "no non-human participant" message that fits the engagement surface.
   */
  recipientMissingMessage?: string;
}

/**
 * Compact composer (textarea + side-by-side Send button) used by every
 * conversation surface. See file header for the full contract.
 */
export function MessageComposer({
  threadId,
  recipient,
  kind = "information",
  onKindChange,
  onSendSuccess,
  placeholder,
  testId = "message-composer",
  recipientMissingMessage,
}: MessageComposerProps) {
  const { toast } = useToast();
  const queryClient = useQueryClient();
  const textareaRef = useRef<HTMLTextAreaElement>(null);
  const [text, setText] = useState("");

  // Focus the textarea when the parent flips into answer mode so the
  // user can start typing the answer without an extra click.
  useEffect(() => {
    if (kind === "answer") {
      textareaRef.current?.focus();
    }
  }, [kind]);

  const send = useMutation<
    Awaited<ReturnType<typeof api.sendMessage>>,
    Error,
    { trimmed: string }
  >({
    mutationFn: async ({ trimmed }) => {
      if (!recipient) {
        throw new Error(
          recipientMissingMessage ??
            "No recipient available — the conversation has no addressable participant.",
        );
      }
      if (threadId) {
        // Only attach `kind` when the caller has explicitly opted into
        // a non-default mode (currently just engagement answer). Default
        // sends omit the field so the wire payload matches the legacy
        // shape used by existing CLI parity tests and server defaults.
        const body =
          kind === "information"
            ? {
                to: { scheme: recipient.scheme, path: recipient.path },
                text: trimmed,
              }
            : {
                to: { scheme: recipient.scheme, path: recipient.path },
                text: trimmed,
                kind,
              };
        return api.sendThreadMessage(threadId, body) as Promise<
          Awaited<ReturnType<typeof api.sendMessage>>
        >;
      }
      return api.sendMessage({
        to: { scheme: recipient.scheme, path: recipient.path },
        type: "Domain",
        threadId: null,
        payload: trimmed,
      });
    },
    onSuccess: (_data, { trimmed }) => {
      setText("");

      // Optimistically inject the just-sent message into the thread
      // detail cache so the timeline renders immediately. Only meaningful
      // when we have an existing thread; for new threads the server's
      // auto-generated id arrives on the next refetch.
      if (threadId) {
        const key = queryKeys.threads.detail(threadId);
        const prev = queryClient.getQueryData<ThreadDetail | null>(key);
        if (prev) {
          const syntheticEvent = {
            id: `optimistic-${Date.now()}`,
            eventType: "MessageReceived",
            source: { address: "human://me", displayName: "me" },
            timestamp: new Date().toISOString(),
            severity: "Info",
            summary: trimmed,
            body: trimmed,
          };
          queryClient.setQueryData<ThreadDetail>(key, {
            ...prev,
            events: [...(prev.events ?? []), syntheticEvent],
          });
        }
      }

      queryClient.invalidateQueries({ queryKey: queryKeys.threads.all });
      queryClient.invalidateQueries({ queryKey: queryKeys.activity.all });
      queryClient.invalidateQueries({ queryKey: queryKeys.threads.inbox() });
      if (threadId) {
        queryClient.invalidateQueries({
          queryKey: queryKeys.threads.detail(threadId),
        });
      }
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
    const trimmed = text.trim();
    if (send.isPending || !trimmed) return;
    send.mutate({ trimmed });
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
  const sendLabel = isAnswerMode ? "Send answer" : "Send";
  const sendAriaLabel = isAnswerMode
    ? "Send answer (⌘/Ctrl+Enter)"
    : "Send message (⌘/Ctrl+Enter)";

  const resolvedPlaceholder =
    placeholder ??
    (isAnswerMode
      ? "Type your answer…"
      : recipient
        ? `Message ${recipient.scheme}://${recipient.path}…`
        : "Type a message…");

  const disabled =
    send.isPending || !text.trim() || !recipient;

  return (
    // shrink-0 keeps the composer at its intrinsic height inside a flex
    // column so the timeline above it owns the only scrollbar (#1552).
    <form
      onSubmit={handleSubmit}
      className={[
        "shrink-0 space-y-2 border-t bg-muted/20 p-3",
        isAnswerMode ? "border-warning/40 bg-warning/5" : "border-border",
      ].join(" ")}
      aria-label={isAnswerMode ? "Answer clarifying question" : "Send message"}
      data-testid={testId}
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
            onClick={() => onKindChange?.("information")}
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
          placeholder={resolvedPlaceholder}
          rows={2}
          className="min-w-0 flex-1 resize-none rounded-md border border-input bg-background px-3 py-2 text-sm shadow-sm focus-visible:outline-none focus-visible:ring-1 focus-visible:ring-ring"
          aria-label={isAnswerMode ? "Your answer" : "Message text"}
          data-testid={`${testId}-input`}
          disabled={send.isPending || !recipient}
        />
        <Button
          type="submit"
          disabled={disabled}
          title={sendTooltip}
          aria-label={sendAriaLabel}
          data-testid={`${testId}-send`}
          className={[
            "h-auto shrink-0 self-stretch px-4",
            isAnswerMode
              ? "bg-warning hover:bg-warning/90 text-warning-foreground"
              : "",
          ].join(" ")}
        >
          {send.isPending ? (
            <Loader2 className="mr-1 h-4 w-4 animate-spin" aria-hidden="true" />
          ) : (
            <Send className="mr-1 h-4 w-4" aria-hidden="true" />
          )}
          {sendLabel}
        </Button>
      </div>
    </form>
  );
}
