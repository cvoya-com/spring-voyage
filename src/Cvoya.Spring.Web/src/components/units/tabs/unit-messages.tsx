"use client";

// Unit Messages tab (EXP-tab-unit-messages, umbrella #815 §2 + §4,
// issue #937).
//
// Master/detail layout: conversation list on the left, selected
// thread's events + reply composer on the right. Selection lives in
// the URL as `?conversation=<id>` so deep-links survive refresh. This
// replaces the old list-only view whose rows linked out to the retired
// `/conversations/[id]` route.
//
// Mirrors the CLI `spring conversation {list,show,send} --unit <name>`
// trio in one surface. A "+ New conversation" button (#980 item 2) lets
// an operator open a fresh thread to this unit without leaving the
// Explorer — the CLI has no matching verb today; portal is the
// leading surface here.

import { useCallback, useState } from "react";
import { useRouter, useSearchParams } from "next/navigation";
import Link from "next/link";
import { MessagesSquare, Plus } from "lucide-react";

import { Badge } from "@/components/ui/badge";
import { Button } from "@/components/ui/button";
import { ConversationDetailPane } from "@/components/conversation/conversation-detail-pane";
import { NewConversationDialog } from "@/components/conversation/new-conversation-dialog";
import { cn, timeAgo } from "@/lib/utils";
import { useConversations } from "@/lib/api/queries";

import { registerTab, type TabContentProps } from "./index";

function UnitMessagesTab({ node }: TabContentProps) {
  const router = useRouter();
  const searchParams = useSearchParams();
  // `node.kind === "Unit"` is guaranteed by the registry — `<DetailPane>`
  // dispatches to `lookupTab(kind, tab)` with `kind` narrowed before
  // this component renders. The belt-and-braces narrowing happens
  // after the hook calls so react-hooks/rules-of-hooks stays happy.
  const { data, isLoading, error } = useConversations({ unit: node.id });
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

  if (node.kind !== "Unit") return null;

  if (isLoading) {
    return (
      <p
        role="status"
        aria-live="polite"
        className="text-sm text-muted-foreground"
        data-testid="tab-unit-messages-loading"
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
        data-testid="tab-unit-messages-error"
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
    <div className="space-y-3" data-testid="tab-unit-messages">
      {header}
      {conversations.length === 0 ? (
        <p
          className="text-sm text-muted-foreground"
          data-testid="tab-unit-messages-empty"
        >
          No conversations for this unit yet.
        </p>
      ) : (
        <div className="grid gap-4 md:grid-cols-[minmax(0,18rem)_1fr]">
          <ConversationList
            conversations={conversations}
            selectedId={selectedId}
            onSelect={setSelected}
            label={`Conversations for unit ${node.name}`}
          />
          <div
            className={cn(
              "flex min-h-[24rem] flex-col rounded-md border border-border bg-background",
            )}
          >
            {selectedId ? (
              <ConversationDetailPane
                conversationId={selectedId}
                selfAddress={`unit://${node.id}`}
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
        targetScheme="unit"
        targetPath={node.id}
        onCreated={(conversationId) => {
          setComposerOpen(false);
          setSelected(conversationId);
        }}
      />
    </div>
  );
}

interface ConversationListProps {
  conversations: ReadonlyArray<{
    id: string;
    summary?: string | null;
    status?: string | null;
    lastActivity?: string | null;
  }>;
  selectedId: string | null;
  onSelect: (id: string) => void;
  label: string;
}

/**
 * Shared master-column renderer. Used by both the Unit and Agent
 * Messages tabs so selection affordance, keyboard focus, and deep-link
 * semantics stay identical.
 */
export function ConversationList({
  conversations,
  selectedId,
  onSelect,
  label,
}: ConversationListProps) {
  return (
    <ul
      className="max-h-[28rem] divide-y divide-border overflow-auto rounded-md border border-border text-sm"
      aria-label={label}
    >
      {conversations.map((c) => {
        const isSelected = c.id === selectedId;
        const href = `?conversation=${encodeURIComponent(c.id)}`;
        return (
          <li key={c.id}>
            <Link
              href={href}
              scroll={false}
              replace
              onClick={(e) => {
                e.preventDefault();
                onSelect(c.id);
              }}
              aria-current={isSelected ? "true" : undefined}
              className={cn(
                "flex items-center gap-3 px-3 py-2",
                isSelected
                  ? "bg-muted/60"
                  : "hover:bg-muted/30 focus-visible:bg-muted/30",
              )}
              data-testid={
                isSelected ? "conversation-row-selected" : "conversation-row"
              }
              data-conversation-id={c.id}
            >
              <MessagesSquare
                className="h-4 w-4 shrink-0 text-muted-foreground"
                aria-hidden="true"
              />
              <span className="min-w-0 flex-1 truncate">
                {c.summary || c.id}
              </span>
              {c.status ? (
                <Badge variant="outline" className="shrink-0">
                  {c.status}
                </Badge>
              ) : null}
              {c.lastActivity ? (
                <span className="shrink-0 text-xs text-muted-foreground">
                  {timeAgo(c.lastActivity)}
                </span>
              ) : null}
            </Link>
          </li>
        );
      })}
    </ul>
  );
}

registerTab("Unit", "Messages", UnitMessagesTab);

export default UnitMessagesTab;
