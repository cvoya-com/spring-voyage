"use client";

import { useState } from "react";
import { useMutation, useQueryClient } from "@tanstack/react-query";
import { Send } from "lucide-react";

import { Button } from "@/components/ui/button";
import { useToast } from "@/components/ui/toast";
import { api } from "@/lib/api/client";
import { queryKeys } from "@/lib/api/query-keys";

import { parseThreadSource } from "./role";

interface ThreadComposerProps {
  threadId: string;
  /**
   * Default `scheme://path` recipient pre-selected in the picker. The
   * Messages tab picks the most-recently-active non-self participant
   * so the common case (replying to the last speaker) is one keystroke.
   */
  defaultRecipient?: string;
  /**
   * Other addresses observed on the thread. Rendered as quick-pick
   * pills above the textarea so the user can re-target without typing.
   */
  participants?: string[];
}

/**
 * Message composer for a conversation thread.
 *
 * The composer is intentionally CLI-shaped: it asks for an `address`
 * and a `text`, the same two arguments `spring conversation send`
 * takes (#452). The form does NOT carry a "from" — the platform infers
 * the sender from the authenticated context, exactly like the CLI.
 *
 * Live updates flow through the activity stream — the SSE handler
 * invalidates `queryKeys.conversations.detail(id)` (see
 * `queryKeysAffectedBySource`), so the new message renders without a
 * manual refetch. We still invalidate explicitly on success as a
 * belt-and-braces fallback for environments where SSE is proxied /
 * disabled.
 */
export function ThreadComposer({
  threadId,
  defaultRecipient,
  participants = [],
}: ThreadComposerProps) {
  const { toast } = useToast();
  const queryClient = useQueryClient();
  // The recipient is seeded once from the default — re-syncing on
  // every render would clobber a user-edited value when the thread
  // refreshes. Users can use the participant pills below to retarget.
  const [recipient, setRecipient] = useState(defaultRecipient ?? "");
  const [text, setText] = useState("");

  const send = useMutation({
    mutationFn: async () => {
      const trimmed = text.trim();
      const target = recipient.trim();
      if (!trimmed) {
        throw new Error("Message text is required.");
      }
      if (!target) {
        throw new Error("Recipient address is required.");
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
      });
    },
    onSuccess: () => {
      setText("");
      queryClient.invalidateQueries({
        queryKey: queryKeys.threads.detail(threadId),
      });
      queryClient.invalidateQueries({ queryKey: queryKeys.threads.all });
      queryClient.invalidateQueries({ queryKey: queryKeys.activity.all });
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

  // Cmd/Ctrl+Enter sends — matches the textbook chat ergonomic and
  // mirrors the CLI's "type then ENTER" affordance for one-line text.
  const handleKeyDown = (e: React.KeyboardEvent<HTMLTextAreaElement>) => {
    if ((e.metaKey || e.ctrlKey) && e.key === "Enter") {
      e.preventDefault();
      submit();
    }
  };

  return (
    <form
      onSubmit={handleSubmit}
      className="space-y-2 border-t border-border bg-muted/20 p-3"
      aria-label="Reply to conversation"
      data-testid="conversation-composer"
    >
      {participants.length > 0 && (
        <div className="flex flex-wrap items-center gap-1 text-xs">
          <span className="text-muted-foreground">To:</span>
          {participants.map((p) => (
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
      <input
        type="text"
        value={recipient}
        onChange={(e) => setRecipient(e.target.value)}
        placeholder="Recipient (scheme://path, e.g. agent://ada)"
        className="flex h-9 w-full rounded-md border border-input bg-background px-3 py-1 font-mono text-xs shadow-sm focus-visible:outline-none focus-visible:ring-1 focus-visible:ring-ring"
        aria-label="Recipient address"
      />
      <textarea
        value={text}
        onChange={(e) => setText(e.target.value)}
        onKeyDown={handleKeyDown}
        placeholder="Type a message…  (⌘/Ctrl+Enter to send)"
        rows={3}
        className="flex min-h-[60px] w-full resize-y rounded-md border border-input bg-background px-3 py-2 text-sm shadow-sm focus-visible:outline-none focus-visible:ring-1 focus-visible:ring-ring"
        aria-label="Message text"
      />
      <div className="flex items-center justify-end gap-2">
        <span className="text-xs text-muted-foreground">
          {send.isPending ? "Sending…" : "⌘/Ctrl+Enter to send"}
        </span>
        <Button
          type="submit"
          size="sm"
          disabled={send.isPending || !text.trim() || !recipient.trim()}
        >
          <Send className="mr-1 h-4 w-4" />
          Send
        </Button>
      </div>
    </form>
  );
}
