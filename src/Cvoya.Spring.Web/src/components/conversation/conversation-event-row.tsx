"use client";

import { useState } from "react";
import { ChevronDown, ChevronRight, Wrench } from "lucide-react";
import Link from "next/link";

import { Badge } from "@/components/ui/badge";
import { cn } from "@/lib/utils";
import type { ConversationEvent } from "@/lib/api/types";

import {
  parseConversationSource,
  ROLE_STYLES,
  roleFromEvent,
  type ConversationRole,
} from "./role";

/**
 * Whether this event type should render as a collapsed call-out by
 * default. Tool calls (`DecisionMade`) and lifecycle events
 * (`StateChanged`, `WorkflowStepCompleted`, `ReflectionCompleted`)
 * start collapsed; messages stay expanded so the thread reads like a
 * chat.
 */
function isCollapsibleByDefault(eventType: string, role: ConversationRole) {
  if (role === "tool") return true;
  return (
    eventType === "StateChanged" ||
    eventType === "WorkflowStepCompleted" ||
    eventType === "ReflectionCompleted"
  );
}

interface ConversationEventRowProps {
  event: ConversationEvent;
}

/**
 * One row in the conversation thread view. Renders the event as a
 * chat-style bubble, aligned left/right by role (human → right, every
 * other role → left). Tool/lifecycle events start collapsed; text
 * messages stay expanded.
 *
 * The row also surfaces a "View in activity" jump-link that opens the
 * activity page filtered to this event's source — useful when an
 * operator wants the full event payload rather than the chat-style
 * summary rendered here.
 */
export function ConversationEventRow({ event }: ConversationEventRowProps) {
  const role = roleFromEvent(event.source, event.eventType);
  const style = ROLE_STYLES[role];
  const source = parseConversationSource(event.source);
  const collapsible = isCollapsibleByDefault(event.eventType, role);
  const [expanded, setExpanded] = useState(!collapsible);

  const timestamp = new Date(event.timestamp);

  return (
    <div
      className={cn(
        "flex w-full",
        style.align === "end" ? "justify-end" : "justify-start",
      )}
      data-testid={`conversation-event-${event.id}`}
      data-role={role}
    >
      <div className={cn("flex max-w-[80%] min-w-0 flex-col gap-1")}>
        <div
          className={cn(
            "flex items-center gap-2 text-xs text-muted-foreground",
            style.align === "end" ? "justify-end" : "justify-start",
          )}
        >
          <Badge variant="outline" className="h-5 px-1.5 text-[10px]">
            {style.label}
          </Badge>
          <span className="truncate font-mono">{source.raw}</span>
          <span aria-hidden="true">·</span>
          <time
            dateTime={event.timestamp}
            title={timestamp.toLocaleString()}
          >
            {timestamp.toLocaleTimeString([], {
              hour: "2-digit",
              minute: "2-digit",
            })}
          </time>
        </div>

        <div
          className={cn(
            "rounded-lg px-3 py-2 text-sm shadow-sm",
            style.bubble,
          )}
        >
          {collapsible ? (
            <button
              type="button"
              onClick={() => setExpanded((v) => !v)}
              className="flex w-full items-center gap-2 text-left"
              aria-expanded={expanded}
            >
              {expanded ? (
                <ChevronDown className="h-3.5 w-3.5 shrink-0" />
              ) : (
                <ChevronRight className="h-3.5 w-3.5 shrink-0" />
              )}
              {role === "tool" && (
                <Wrench className="h-3.5 w-3.5 shrink-0" aria-hidden="true" />
              )}
              <span className="min-w-0 flex-1 truncate">
                {role === "tool" ? "Tool call" : event.eventType}
                {event.summary ? ` — ${event.summary}` : ""}
              </span>
            </button>
          ) : (
            <p className="whitespace-pre-wrap break-words">{event.summary}</p>
          )}

          {expanded && collapsible && (
            <div className="mt-2 space-y-1 rounded border border-black/5 bg-background/40 p-2 text-xs">
              <p className="whitespace-pre-wrap break-words">
                {event.summary}
              </p>
              <p className="text-muted-foreground">
                {event.eventType} · {event.severity}
              </p>
            </div>
          )}
        </div>

        <div
          className={cn(
            "flex items-center gap-2 text-[11px] text-muted-foreground",
            style.align === "end" ? "justify-end" : "justify-start",
          )}
        >
          <Link
            href={`/activity?source=${encodeURIComponent(source.raw)}`}
            className="hover:underline"
            aria-label="Open in activity log"
          >
            View in activity →
          </Link>
        </div>
      </div>
    </div>
  );
}
