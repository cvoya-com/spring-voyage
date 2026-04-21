"use client";

// Agent Messages tab (EXP-tab-agent-messages, umbrella #815 §4).
//
// Conversation jump-list filtered to this agent. Mirrors
// `spring conversation list --agent <name>` and the unit-side Messages
// tab.

import Link from "next/link";
import { MessagesSquare } from "lucide-react";

import { Badge } from "@/components/ui/badge";
import { useConversations } from "@/lib/api/queries";
import { timeAgo } from "@/lib/utils";

import { registerTab, type TabContentProps } from "./index";

function AgentMessagesTab({ node }: TabContentProps) {
  const { data, isLoading, error } = useConversations({ agent: node.id });
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

  if (conversations.length === 0) {
    return (
      <p
        className="text-sm text-muted-foreground"
        data-testid="tab-agent-messages-empty"
      >
        No conversations involving this agent yet.
      </p>
    );
  }

  return (
    <ul
      className="divide-y divide-border rounded-md border border-border text-sm"
      data-testid="tab-agent-messages"
      aria-label={`Conversations for agent ${node.name}`}
    >
      {conversations.map((c) => (
        <li
          key={c.id}
          className="flex items-center gap-3 px-3 py-2"
        >
          <MessagesSquare
            className="h-4 w-4 shrink-0 text-muted-foreground"
            aria-hidden="true"
          />
          <Link
            href={`/conversations/${encodeURIComponent(c.id)}`}
            className="min-w-0 flex-1 truncate hover:underline"
          >
            {c.summary || c.id}
          </Link>
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
        </li>
      ))}
    </ul>
  );
}

registerTab("Agent", "Messages", AgentMessagesTab);

export default AgentMessagesTab;
