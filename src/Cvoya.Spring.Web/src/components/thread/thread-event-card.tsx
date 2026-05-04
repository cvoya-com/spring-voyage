"use client";

// Compact "event card" for non-message events in a thread timeline (#1630).
//
// The participant view of a thread reads like a chat (left/right bubbles
// for human ↔ agent messages). Non-message events (a tool call, a state
// change, a workflow step) don't fit that metaphor — rendering them as
// bubbles makes the timeline read as if the system is talking to the
// user. This component renders them as a compact card with friendly,
// UX-first copy instead:
//
//   ┌──────────────────────────────────────────────────────┐
//   │  ⚙  StateChanged · ada · 14:03                  ⌃    │
//   │     Agent finished its current step.                  │
//   └──────────────────────────────────────────────────────┘
//
// Click anywhere on the row (or the chevron) to expand the card; the
// expanded view exposes the raw envelope (event id, type, source/from
// addresses, severity, summary line) for diagnostic use.
//
// Click-to-expand is local state (no router round-trip) so the user can
// open many cards at once and the page state remains stable across SSE
// refetches.

import { useState } from "react";
import {
  AlertTriangle,
  ChevronDown,
  ChevronRight,
  Cog,
  Info,
  ListTree,
  MessageSquare,
  Wrench,
} from "lucide-react";

import { Badge } from "@/components/ui/badge";
import { cn } from "@/lib/utils";
import type { ThreadEvent } from "@/lib/api/types";

import {
  participantDisplayName,
  parseThreadSource,
  roleFromEvent,
} from "./role";

/**
 * Friendly label + icon for the most common event types. Anything not
 * in the table falls back to the eventType string as-is and the generic
 * `ListTree` icon.
 */
const EVENT_PRESENTATION: Record<
  string,
  { label: string; icon: typeof Info; tone: "neutral" | "info" | "warning" | "destructive" }
> = {
  MessageReceived: { label: "Message", icon: MessageSquare, tone: "info" },
  MessageSent: { label: "Message", icon: MessageSquare, tone: "info" },
  ThreadStarted: { label: "Engagement started", icon: ListTree, tone: "neutral" },
  ThreadCompleted: { label: "Engagement completed", icon: ListTree, tone: "neutral" },
  DecisionMade: { label: "Tool call", icon: Wrench, tone: "warning" },
  StateChanged: { label: "State changed", icon: Cog, tone: "neutral" },
  WorkflowStepCompleted: { label: "Workflow step", icon: Cog, tone: "neutral" },
  ReflectionCompleted: { label: "Reflection", icon: Cog, tone: "neutral" },
  CostIncurred: { label: "Cost", icon: Cog, tone: "neutral" },
  TokenDelta: { label: "Token usage", icon: Cog, tone: "neutral" },
  ValidationProgress: { label: "Validation", icon: Cog, tone: "neutral" },
  InitiativeTriggered: { label: "Initiative", icon: ListTree, tone: "neutral" },
  ErrorOccurred: { label: "Error", icon: AlertTriangle, tone: "destructive" },
};

/**
 * Best-effort friendly summary. Prefers the body when present, then the
 * engine summary, then the event-type-specific friendly label.
 *
 * Historical note: this used to strip a `"Received Domain message <uuid>
 * from <address>"` envelope template that the platform emitted as the
 * receive-event summary. That envelope was removed upstream in #1641, so
 * the strip is gone. If a card ever surfaces text matching that template
 * again, the fix is on the platform side, not here (#1639).
 */
function friendlySummary(event: ThreadEvent, fallbackLabel: string): string {
  const body = event.body?.trim();
  if (body) return body;
  const summary = event.summary?.trim();
  if (summary) return summary;
  return fallbackLabel;
}

const TONE_BUBBLE: Record<string, string> = {
  neutral: "border-border bg-muted/40",
  info: "border-primary/30 bg-primary/5",
  warning: "border-amber-200 bg-amber-50 text-amber-900",
  destructive: "border-destructive/40 bg-destructive/10 text-foreground",
};

export interface ThreadEventCardProps {
  event: ThreadEvent;
  /** Test-id prefix for the row (defaults to `conversation-event`). */
  testIdPrefix?: string;
  /**
   * When true, force-expand the technical details panel on first render.
   * Used by tests; consumers should leave this off so users land on the
   * compact card.
   */
  defaultExpanded?: boolean;
}

/**
 * Render a non-message thread event as a compact, click-to-expand card.
 * The collapsed state shows: icon, friendly label, source name, time, and
 * a one-line summary. The expanded state exposes the raw envelope (event
 * id, type, source / from addresses, severity, full summary) for
 * diagnostic use.
 */
export function ThreadEventCard({
  event,
  testIdPrefix = "conversation-event",
  defaultExpanded = false,
}: ThreadEventCardProps) {
  const presentation =
    EVENT_PRESENTATION[event.eventType] ??
    {
      label: event.eventType,
      icon: ListTree,
      tone: "neutral" as const,
    };
  const Icon = presentation.icon;
  // The card always attributes to `event.from` when present (the underlying
  // sender) — same convention as <ThreadEventRow>. Without this, every
  // receive event would list the receiving actor as the source.
  const attributed = event.from ?? event.source;
  const sourceName = participantDisplayName(attributed);
  const role = roleFromEvent(
    typeof attributed === "string" ? attributed : (attributed?.address ?? ""),
    event.eventType,
  );

  const [expanded, setExpanded] = useState(defaultExpanded);
  const timestamp = new Date(event.timestamp);
  const summaryLine = friendlySummary(event, presentation.label);

  // Severity escalation: a card whose underlying event is an error or
  // warning takes the destructive/warning tone regardless of event type
  // — operators should never have to read the eventType to know
  // something went wrong.
  const severity = event.severity ?? "Info";
  const tone =
    severity === "Error"
      ? "destructive"
      : severity === "Warning"
        ? "warning"
        : presentation.tone;

  const sourceAddress = parseThreadSource(
    typeof attributed === "string"
      ? attributed
      : (attributed?.address ?? ""),
  ).raw;
  const fromAddress = event.from
    ? parseThreadSource(
        typeof event.from === "string"
          ? event.from
          : (event.from?.address ?? ""),
      ).raw
    : null;

  return (
    <div
      className={cn(
        "rounded-md border px-3 py-2 text-sm shadow-sm transition-colors",
        TONE_BUBBLE[tone],
      )}
      data-testid={`${testIdPrefix}-card-${event.id}`}
      data-role={role}
      data-event-type={event.eventType}
    >
      <button
        type="button"
        onClick={() => setExpanded((v) => !v)}
        aria-expanded={expanded}
        aria-controls={`${testIdPrefix}-card-${event.id}-details`}
        data-testid={`${testIdPrefix}-card-${event.id}-toggle`}
        className="flex w-full items-start gap-2 text-left"
      >
        {expanded ? (
          <ChevronDown
            className="mt-0.5 h-3.5 w-3.5 shrink-0 text-muted-foreground"
            aria-hidden="true"
          />
        ) : (
          <ChevronRight
            className="mt-0.5 h-3.5 w-3.5 shrink-0 text-muted-foreground"
            aria-hidden="true"
          />
        )}
        <Icon
          className="mt-0.5 h-3.5 w-3.5 shrink-0 text-muted-foreground"
          aria-hidden="true"
        />
        <div className="flex min-w-0 flex-1 flex-col gap-0.5">
          <div className="flex items-center gap-2 text-xs text-muted-foreground">
            <Badge variant="outline" className="h-5 px-1.5 text-[10px]">
              {presentation.label}
            </Badge>
            {sourceName && (
              <span
                className="truncate font-medium text-foreground/80"
                data-testid={`${testIdPrefix}-card-source-name`}
              >
                {sourceName}
              </span>
            )}
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
          <p
            className="whitespace-pre-wrap break-words text-sm text-foreground/90"
            data-testid={`${testIdPrefix}-card-summary`}
          >
            {summaryLine}
          </p>
        </div>
      </button>

      {expanded && (
        <div
          id={`${testIdPrefix}-card-${event.id}-details`}
          className="mt-2 space-y-0.5 rounded border border-border/60 bg-background/60 p-2 font-mono text-[11px] text-muted-foreground"
          data-testid={`${testIdPrefix}-card-${event.id}-details`}
        >
          <p>
            <span className="text-foreground">id</span> {event.id}
          </p>
          <p>
            <span className="text-foreground">type</span> {event.eventType}
          </p>
          <p>
            <span className="text-foreground">source</span> {sourceAddress}
          </p>
          {fromAddress && (
            <p>
              <span className="text-foreground">from</span> {fromAddress}
            </p>
          )}
          <p>
            <span className="text-foreground">severity</span> {severity}
          </p>
          {event.summary && (
            <p className="break-words">
              <span className="text-foreground">summary</span> {event.summary}
            </p>
          )}
        </div>
      )}
    </div>
  );
}

/**
 * Heuristic: should this event render as a card rather than a chat-style
 * message bubble? Cards are for non-conversational events (lifecycle,
 * tool calls, errors). Message events always render as bubbles — the
 * platform now populates `event.body` (or a non-leaky `event.summary`
 * placeholder) for every projected message event, so the bubble path
 * always has usable text (#1641 / #1639).
 */
export function shouldRenderAsCard(event: ThreadEvent): boolean {
  return (
    event.eventType !== "MessageReceived" && event.eventType !== "MessageSent"
  );
}
