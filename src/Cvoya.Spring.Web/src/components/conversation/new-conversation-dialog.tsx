"use client";

import { useState } from "react";
import { useMutation, useQueryClient } from "@tanstack/react-query";

import { Button } from "@/components/ui/button";
import { Dialog } from "@/components/ui/dialog";
import { api } from "@/lib/api/client";
import { queryKeys } from "@/lib/api/query-keys";

interface NewConversationDialogProps {
  open: boolean;
  onClose: () => void;
  /** Target address scheme — "unit" or "agent". */
  targetScheme: "unit" | "agent";
  /** Target address path — the unit or agent slug. */
  targetPath: string;
  /**
   * Called with the new conversation id once the server round-trips a
   * `MessageResponse` with `conversationId` populated. The caller is
   * expected to route to the thread view so the user lands on the
   * freshly-opened conversation.
   */
  onCreated: (conversationId: string) => void;
}

/**
 * Modal composer for the Explorer Messages tab's "+ New conversation"
 * affordance (#980 item 2). Posts to `POST /api/v1/messages` with the
 * hosting unit/agent as the `to` address, `type: "Domain"`, and the
 * typed body as the `payload`; no `conversationId` is supplied — the
 * server's #985 auto-gen assigns a fresh UUID and returns it on
 * `MessageResponse.conversationId`.
 */
export function NewConversationDialog({
  open,
  onClose,
  targetScheme,
  targetPath,
  onCreated,
}: NewConversationDialogProps) {
  const queryClient = useQueryClient();
  const [body, setBody] = useState("");

  const send = useMutation({
    mutationFn: async () => {
      const trimmed = body.trim();
      if (!trimmed) {
        throw new Error("Message body is required.");
      }
      return api.sendMessage({
        to: { scheme: targetScheme, path: targetPath },
        type: "Domain",
        conversationId: null,
        payload: trimmed,
      });
    },
    onSuccess: (data) => {
      // The server auto-assigns a conversation id for Domain messages
      // that didn't carry one — trust it, but guard the branch so a
      // future wire change doesn't throw here.
      if (data.conversationId) {
        queryClient.invalidateQueries({ queryKey: queryKeys.conversations.all });
        queryClient.invalidateQueries({ queryKey: queryKeys.activity.all });
        setBody("");
        onCreated(data.conversationId);
      }
    },
  });

  const handleClose = () => {
    if (send.isPending) return;
    send.reset();
    setBody("");
    onClose();
  };

  const submit = (e: React.FormEvent) => {
    e.preventDefault();
    if (send.isPending) return;
    send.mutate();
  };

  const targetLabel = `${targetScheme}://${targetPath}`;
  const errorMessage =
    send.error instanceof Error ? send.error.message : send.error ? String(send.error) : null;

  return (
    <Dialog
      open={open}
      onClose={handleClose}
      title="Start a new conversation"
      description={`Send the first message to ${targetLabel}. A fresh thread is opened for you.`}
      footer={
        <>
          <Button
            variant="outline"
            type="button"
            onClick={handleClose}
            disabled={send.isPending}
            data-testid="new-conversation-cancel"
          >
            Cancel
          </Button>
          <Button
            type="submit"
            form="new-conversation-form"
            disabled={send.isPending || !body.trim()}
            data-testid="new-conversation-submit"
          >
            {send.isPending ? "Sending…" : "Start conversation"}
          </Button>
        </>
      }
    >
      <form
        id="new-conversation-form"
        onSubmit={submit}
        className="space-y-2"
        aria-label="New conversation composer"
      >
        <label
          htmlFor="new-conversation-body"
          className="block text-xs font-medium text-muted-foreground"
        >
          First message
        </label>
        <textarea
          id="new-conversation-body"
          value={body}
          onChange={(e) => setBody(e.target.value)}
          placeholder="Type the opening message…"
          rows={5}
          className="flex min-h-[96px] w-full resize-y rounded-md border border-input bg-background px-3 py-2 text-sm shadow-sm focus-visible:outline-none focus-visible:ring-1 focus-visible:ring-ring"
          data-testid="new-conversation-body"
          disabled={send.isPending}
        />
        {errorMessage && (
          <p
            role="alert"
            className="rounded-md border border-destructive/50 bg-destructive/10 px-3 py-2 text-xs text-destructive"
            data-testid="new-conversation-error"
          >
            {errorMessage}
          </p>
        )}
      </form>
    </Dialog>
  );
}
