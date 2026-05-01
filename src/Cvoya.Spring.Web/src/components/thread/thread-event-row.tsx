"use client";

import { useState } from "react";
import { AlertTriangle, ChevronDown, ChevronRight, Wrench } from "lucide-react";
import Link from "next/link";

import { Badge } from "@/components/ui/badge";
import { cn } from "@/lib/utils";
import type { ThreadEvent } from "@/lib/api/types";

import {
  parseThreadSource,
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

/**
 * #1161: whether an event represents a dispatch failure or system error
 * that should render inline in the conversation with destructive styling
 * rather than collapsing into the neutral "system" call-out. Errors
 * must never be hidden by default — the user is in the conversation and
 * cannot be expected to hunt the activity log for failure context.
 */
function isErrorEvent(eventType: string, severity: string): boolean {
  return eventType === "ErrorOccurred" || severity === "Error";
}

interface ThreadEventRowProps {
  event: ThreadEvent;
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
 *
 * #1209: when the event carries a message body (the projection now
 * stamps it on every `MessageReceived` event), render the body in
 * place of the envelope summary so the thread reads as a real
 * conversation rather than a list of "Received Domain message X from Y"
 * lines.
 *
 * #1482: message bubble header shows the display name (path of the source
 * address) rather than the full address. The full address is available via
 * the "View in activity" jump-link or the (i) toggle in the inbox view.
 */
export function ThreadEventRow({ event }: ThreadEventRowProps) {
  const role = roleFromEvent(event.source.address, event.eventType);
  const style = ROLE_STYLES[role];
  const source = parseThreadSource(event.source.address);
  const collapsible = isCollapsibleByDefault(event.eventType, role);
  const [expanded, setExpanded] = useState(!collapsible);

  const timestamp = new Date(event.timestamp);
  const bodyText =
    event.eventType === "MessageReceived" && event.body ? event.body : null;

  // Display name: prefer the API-enriched displayName, fall back to the
  // path portion of the source address (e.g. "agent://engineering/ada" →
  // "engineering/ada"), then the raw address string.
  const sourceDisplayName = event.source.displayName || source.path || source.raw;

  // #1161: error events render with destructive styling and are never
  // collapsed — the user cannot be expected to open the activity log to
  // discover a dispatch failure that happened inside their active
  // conversation. The alert-triangle icon signals the error visually
  // without relying solely on colour (WCAG 1.4.1).
  const isError = isErrorEvent(event.eventType, event.severity ?? "");

  if (isError) {
    return (
      <div
        className="flex w-full justify-start"
        data-testid={`conversation-event-${event.id}`}
        data-role="error"
      >
        <div className="flex max-w-[80%] min-w-0 flex-col gap-1">
          <div className="flex items-center gap-2 text-xs text-muted-foreground">
            <Badge
              variant="destructive"
              className="h-5 px-1.5 text-[10px]"
            >
              Error
            </Badge>
            <span className="truncate font-medium text-foreground/80" data-testid="conversation-event-source-name">{sourceDisplayName}</span>
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
            role="alert"
            className="flex items-start gap-2 rounded-lg border border-destructive/50 bg-destructive/10 px-3 py-2 text-sm text-foreground shadow-sm"
          >
            <AlertTriangle
              className="mt-0.5 h-4 w-4 shrink-0 text-destructive"
              aria-hidden="true"
            />
            <p className="whitespace-pre-wrap break-words">{event.summary}</p>
          </div>
          <div className="flex items-center gap-2 text-[11px] text-muted-foreground">
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
          <span className="truncate font-medium text-foreground/80" data-testid="conversation-event-source-name">{sourceDisplayName}</span>
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
            <p className="whitespace-pre-wrap break-words">
              {bodyText ?? event.summary}
            </p>
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
