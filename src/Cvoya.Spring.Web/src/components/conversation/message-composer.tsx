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

import { useEffect, useMemo, useRef, useState } from "react";
import { useMutation, useQueryClient } from "@tanstack/react-query";
import { Loader2, MessageCircleQuestion, Send, UserRoundX } from "lucide-react";

import { Badge } from "@/components/ui/badge";
import { Button } from "@/components/ui/button";
import { useToast } from "@/components/ui/toast";
import { api } from "@/lib/api/client";
import { useCallerHumans } from "@/lib/api/queries";
import { queryKeys } from "@/lib/api/query-keys";
import { formatTranslatedError } from "@/lib/api/translate-error";
import type { ThreadDetail } from "@/lib/api/types";
import {
  HumanFromSelector,
  pickDefaultHumanId,
} from "./human-from-selector";

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
  /**
   * Optional "speaking as" Hat hint (ADR-0062 § 5). For reply
   * composers this is the Hat the thread came in on (resolved by the
   * parent from the thread's inbound `Message.To`); for new-outbound
   * composers leave undefined so the selector defaults to the
   * caller's primary Hat. When the caller has only one bound Hat the
   * selector collapses to a static badge and this hint is ignored.
   */
  defaultHumanId?: string | null;
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
  defaultHumanId,
}: MessageComposerProps) {
  const { toast } = useToast();
  const queryClient = useQueryClient();
  const textareaRef = useRef<HTMLTextAreaElement>(null);
  const [text, setText] = useState("");
  const [isQueuing, setIsQueuing] = useState(false);

  // ADR-0062 § 3 + § 5: pull the caller's bound-Hat set so the user
  // can pick which Hat the outbound is stamped with. The selected id
  // is forwarded as `from: <guid>` on the POST body; the server
  // validates membership in the caller's bound set. The query is
  // cheap (one round-trip per session) and the selector hides itself
  // for callers with one or zero Hats so the composer's chrome stays
  // minimal in the OSS single-operator case.
  // #2972: scope the Hat set to the recipient so the from-selector lists
  // only the Hats that can reach this unit/agent under the Hat ↔ unit
  // reachability rule. Only unit/agent targets are gated; other schemes
  // (or no recipient) fall back to the full bound set.
  const scopeRecipient =
    recipient && (recipient.scheme === "unit" || recipient.scheme === "agent")
      ? `${recipient.scheme}:${recipient.path}`
      : null;
  const callerHumansQuery = useCallerHumans({ recipient: scopeRecipient });
  const callerHumans = useMemo(
    () => callerHumansQuery.data ?? [],
    [callerHumansQuery.data],
  );

  // #2972: when scoped to a unit/agent and the wearable set resolves
  // empty, the operator wears no Hat that can message this recipient —
  // the server would reject the send with 403 NoReachableHat — so block
  // the composer with an explanation instead of letting the send fail.
  const noWearableHat =
    scopeRecipient !== null &&
    callerHumansQuery.isSuccess &&
    callerHumans.length === 0;

  // The selected Hat id. The default-resolution rule is "thread Hat
  // wins (reply default) → primary Hat (new-outbound default) →
  // first bound Hat (fallback)". `pickDefaultHumanId` encodes it; the
  // computed default repins whenever the bound set or thread hint
  // changes so opening a different thread seeds the selector with
  // that thread's pinned Hat. User overrides land on `overrideHumanId`
  // and are scoped per-thread via the keyed reset below.
  const computedDefaultHumanId = useMemo(
    () => pickDefaultHumanId(callerHumans, defaultHumanId ?? null),
    [callerHumans, defaultHumanId],
  );
  const [overrideByThread, setOverrideByThread] = useState<{
    threadId: string | null;
    humanId: string;
  } | null>(null);
  const selectedHumanId =
    overrideByThread !== null && overrideByThread.threadId === threadId
      ? overrideByThread.humanId
      : computedDefaultHumanId;
  const handleSelectHuman = (humanId: string) => {
    setOverrideByThread({ threadId, humanId });
  };

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
    { trimmed: string },
    { previousThread?: ThreadDetail | null; trimmed: string }
  >({
    mutationFn: async ({ trimmed }) => {
      if (!recipient) {
        throw new Error(
          recipientMissingMessage ??
            "No recipient available — the conversation has no addressable participant.",
        );
      }
      // ADR-0062 § 3: stamp the selected Hat as the explicit `from`
      // field. The server validates it against the caller's bound
      // set; null/omitted falls back to the server-side resolution
      // (thread-pinned → primary → any bound → 400 NoBoundHuman).
      const fromHumanId = selectedHumanId ?? undefined;
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
                from: fromHumanId,
              }
            : {
                to: { scheme: recipient.scheme, path: recipient.path },
                text: trimmed,
                kind,
                from: fromHumanId,
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
        from: fromHumanId,
      });
    },
    onMutate: async ({ trimmed }) => {
      // Cancel any in-flight refetch so it doesn't overwrite the optimistic event.
      if (threadId) {
        await queryClient.cancelQueries({
          queryKey: queryKeys.threads.detail(threadId),
        });
      }

      // Clear the textarea and release the button in the same batch as
      // the cache inject below — all three happen in one React render.
      setText("");
      setIsQueuing(false);

      // Inject a synthetic event so the timeline shows the message before
      // the server acknowledges it. Only meaningful for existing threads;
      // new-thread sends pick up the real event on the next refetch.
      if (threadId) {
        const key = queryKeys.threads.detail(threadId);
        const prev = queryClient.getQueryData<ThreadDetail | null>(key);
        if (prev) {
          // #2082: ParticipantRef.id is required on the contract. The
          // optimistic event is a placeholder that the refetch replaces
          // with the real, server-resolved event.
          const syntheticEvent = {
            id: `optimistic-${Date.now()}`,
            eventType: "MessageArrived",
            source: {
              id: "00000000-0000-0000-0000-000000000000",
              address: "human://me",
              displayName: "me",
            },
            timestamp: new Date().toISOString(),
            severity: "Info",
            summary: trimmed,
            body: trimmed,
          };
          queryClient.setQueryData<ThreadDetail>(key, {
            ...prev,
            events: [...(prev.events ?? []), syntheticEvent],
          });
          return { previousThread: prev, trimmed };
        }
      }
      return { trimmed };
    },
    onSuccess: () => {
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
    onError: (err, _vars, context) => {
      setIsQueuing(false);
      // Roll back the optimistic thread update.
      if (context?.previousThread && threadId) {
        queryClient.setQueryData(
          queryKeys.threads.detail(threadId),
          context.previousThread,
        );
      }
      // Restore the composed text so the user doesn't lose their work.
      if (context?.trimmed) {
        setText(context.trimmed);
      }
      toast({
        title: "Failed to send message",
        description: formatTranslatedError(err),
        variant: "destructive",
      });
    },
  });

  const submit = () => {
    const trimmed = text.trim();
    if (send.isPending || isQueuing || !trimmed) return;
    setIsQueuing(true);
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
  const sendTooltip = isQueuing ? "Sending…" : "⌘/Ctrl+Enter to send";
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

  const disabled = isQueuing || !text.trim() || !recipient || noWearableHat;

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

      {/* #2972: reachability gate — the operator wears no Hat that can
          message this recipient. Block the composer with an explanation
          rather than letting the send fail server-side. */}
      {noWearableHat && (
        <div
          className="flex items-start gap-1.5 text-xs text-muted-foreground"
          data-testid={`${testId}-no-hat`}
        >
          <UserRoundX className="h-3.5 w-3.5 shrink-0 mt-0.5" aria-hidden="true" />
          <span>
            You have no Hat that can message{" "}
            {recipient ? (
              <span className="font-medium text-foreground">
                {recipient.scheme}://{recipient.path}
              </span>
            ) : (
              "this recipient"
            )}
            . A unit or agent is reachable only through a human member of the
            unit it belongs to.
          </span>
        </div>
      )}

      {/* ADR-0062 § 5: from-selector strip. Renders nothing when the
          caller has no bound Hats; collapses to a static badge when
          the caller has one Hat; renders the dropdown when 2+. The
          strip sits above the textarea so it reads as a "speaking
          as ..." prefix to the message body. */}
      {callerHumans.length > 0 && (
        <HumanFromSelector
          humans={callerHumans}
          value={selectedHumanId}
          onChange={handleSelectHuman}
          testId={`${testId}-from`}
          disabled={isQueuing || !recipient}
        />
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
          disabled={isQueuing || !recipient || noWearableHat}
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
          {isQueuing ? (
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
