"use client";

// Unit Messages tab (EXP-tab-unit-messages, umbrella #815 §4).
//
// Lists conversations filtered to this unit. Mirrors the CLI's
// `spring conversation list --unit <name>`. Each row links straight
// into the dedicated `/conversations/<id>` surface — this tab is a
// quick-access jump list, not a full conversation inspector.

import Link from "next/link";
import { MessagesSquare } from "lucide-react";

import { Badge } from "@/components/ui/badge";
import { useConversations } from "@/lib/api/queries";
import { timeAgo } from "@/lib/utils";

import { registerTab, type TabContentProps } from "./index";

function UnitMessagesTab({ node }: TabContentProps) {
  // `node.kind === "Unit"` is guaranteed by the registry — `<DetailPane>`
  // dispatches to `lookupTab(kind, tab)` with `kind` narrowed before
  // this component renders. The belt-and-braces narrowing happens
  // after the hook call so react-hooks/rules-of-hooks stays happy.
  const { data, isLoading, error } = useConversations({ unit: node.id });
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

  if (conversations.length === 0) {
    return (
      <p
        className="text-sm text-muted-foreground"
        data-testid="tab-unit-messages-empty"
      >
        No conversations for this unit yet.
      </p>
    );
  }

  return (
    <ul
      className="divide-y divide-border rounded-md border border-border text-sm"
      data-testid="tab-unit-messages"
      aria-label={`Conversations for unit ${node.name}`}
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

registerTab("Unit", "Messages", UnitMessagesTab);

export default UnitMessagesTab;
