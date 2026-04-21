"use client";

// Agent Messages tab (EXP-tab-agent-messages, umbrella #815 §2 + §4,
// issue #937).
//
// Master/detail layout: conversation list on the left, selected
// thread's events + reply composer on the right. Selection lives in
// the URL as `?conversation=<id>` so deep-links survive refresh. This
// replaces the old list-only view whose rows linked out to the
// retired `/conversations/[id]` route.
//
// Mirrors the CLI `spring conversation {list,show,send} --agent <name>`
// trio in one surface. A "+ New conversation" button (#980 item 2) lets
// an operator open a fresh thread to this agent without leaving the
// Explorer.

import { useCallback, useState } from "react";
import { useRouter, useSearchParams } from "next/navigation";
import { Plus } from "lucide-react";

import { Button } from "@/components/ui/button";
import { ConversationDetailPane } from "@/components/conversation/conversation-detail-pane";
import { NewConversationDialog } from "@/components/conversation/new-conversation-dialog";
import { cn } from "@/lib/utils";
import { useConversations } from "@/lib/api/queries";

import { ConversationList } from "./unit-messages";
import { registerTab, type TabContentProps } from "./index";

function AgentMessagesTab({ node }: TabContentProps) {
  const router = useRouter();
  const searchParams = useSearchParams();
  const { data, isLoading, error } = useConversations({ agent: node.id });
  const [composerOpen, setComposerOpen] = useState(false);

  const selectedId = searchParams.get("conversation");

  const setSelected = useCallback(
    (id: string | null) => {
      const params = new URLSearchParams(searchParams.toString());
      if (id) {
        params.set("conversation", id);
      } else {
        params.delete("conversation");
      }
      const qs = params.toString();
      router.replace(qs ? `?${qs}` : "?", { scroll: false });
    },
    [router, searchParams],
  );

  if (node.kind !== "Agent") return null;

  if (isLoading) {
    return (
      <p
        role="status"
        aria-live="polite"
        className="text-sm text-muted-foreground"
        data-testid="tab-agent-messages-loading"
      >
        Loading conversations…
      </p>
    );
  }

  if (error) {
    return (
      <p
        role="alert"
        className="rounded-md border border-destructive/50 bg-destructive/10 px-3 py-2 text-sm text-destructive"
        data-testid="tab-agent-messages-error"
      >
        Couldn&apos;t load conversations:{" "}
        {error instanceof Error ? error.message : String(error)}
      </p>
    );
  }

  const conversations = data ?? [];

  const header = (
    <div className="flex items-center justify-between gap-2">
      <h2 className="text-sm font-medium text-muted-foreground">
        {conversations.length === 0
          ? "No conversations yet"
          : `${conversations.length} conversation${conversations.length === 1 ? "" : "s"}`}
      </h2>
      <Button
        size="sm"
        onClick={() => setComposerOpen(true)}
        data-testid="new-conversation-trigger"
      >
        <Plus className="mr-1 h-4 w-4" aria-hidden="true" />
        New conversation
      </Button>
    </div>
  );

  return (
    <div className="space-y-3" data-testid="tab-agent-messages">
      {header}
      {conversations.length === 0 ? (
        <p
          className="text-sm text-muted-foreground"
          data-testid="tab-agent-messages-empty"
        >
          No conversations involving this agent yet.
        </p>
      ) : (
        <div className="grid gap-4 md:grid-cols-[minmax(0,18rem)_1fr]">
          <ConversationList
            conversations={conversations}
            selectedId={selectedId}
            onSelect={setSelected}
            label={`Conversations for agent ${node.name}`}
          />
          <div
            className={cn(
              "flex min-h-[24rem] flex-col rounded-md border border-border bg-background",
            )}
          >
            {selectedId ? (
              <ConversationDetailPane
                conversationId={selectedId}
                selfAddress={`agent://${node.id}`}
              />
            ) : (
              <p className="m-3 text-sm text-muted-foreground">
                Select a conversation from the list to see its events and
                reply.
              </p>
            )}
          </div>
        </div>
      )}
      <NewConversationDialog
        open={composerOpen}
        onClose={() => setComposerOpen(false)}
        targetScheme="agent"
        targetPath={node.id}
        onCreated={(conversationId) => {
          setComposerOpen(false);
          setSelected(conversationId);
        }}
      />
    </div>
  );
}

registerTab("Agent", "Messages", AgentMessagesTab);

export default AgentMessagesTab;
