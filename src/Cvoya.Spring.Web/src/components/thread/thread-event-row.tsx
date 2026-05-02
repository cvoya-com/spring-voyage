"use client";

import { useState } from "react";
import {
  AlertTriangle,
  ChevronDown,
  ChevronRight,
  Info,
  Wrench,
} from "lucide-react";
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

/**
 * Footer affordance for a row.
 *  - `activity-link` (default): a subtle "View in activity →" link below
 *    the bubble. Used by the engagement portal and the unit/agent
 *    Messages tab where the activity log is the right escalation surface.
 *  - `metadata`: an (i) toggle in the header that reveals a compact
 *    metadata panel (event id / type / source / severity / summary)
 *    below the bubble. Used by the inbox where the user is already in
 *    the conversation surface and the activity log is one click away
 *    via the global nav.
 *  - `none`: no footer affordance.
 */
export type ThreadEventRowActions = "activity-link" | "metadata" | "none";

interface ThreadEventRowProps {
  event: ThreadEvent;
  /** Footer affordance (see {@link ThreadEventRowActions}). */
  actions?: ThreadEventRowActions;
  /** Override for the row's `data-testid` prefix. */
  testIdPrefix?: string;
}

/**
 * One row in the conversation thread view. Renders the event as a
 * chat-style bubble, aligned left/right by role (human → right, every
 * other role → left). Tool/lifecycle events start collapsed; text
 * messages stay expanded.
 *
 * For `MessageReceived` events the bubble represents the *sender* of the
 * underlying message — i.e. `event.from` — not `event.source` (which is
 * the receiving actor that emitted the event). Without this distinction
 * a message from agent://qa-engineer to a human would render as a
 * right-aligned "human-sent" bubble because the human's actor projected
 * the receive event. For all other event types, source is the canonical
 * attribution.
 *
 * If the event carries a message body, render the body in place of the
 * envelope summary so the thread reads as a real conversation rather
 * than a list of "Received Domain message X from Y" lines.
 *
 * Header row rules (#1502):
 *  - Human bubbles: omit the role badge entirely. Show displayName only
 *    when it is non-empty (no fallback to raw address / UUID).
 *  - Agent / unit / system bubbles: keep the role badge; show displayName
 *    (or the address path as a readable fallback).
 */
export function ThreadEventRow({
  event,
  actions = "activity-link",
  testIdPrefix = "conversation-event",
}: ThreadEventRowProps) {
  // Attribute MessageReceived bubbles to the sender (event.from) rather
  // than the receiver-projected event.source.
  const isMessageReceived = event.eventType === "MessageReceived";
  const attributed =
    isMessageReceived && event.from ? event.from : event.source;

  // `attributed` may be a ParticipantRef object (address + displayName)
  // or a plain address string when served by an older API version.

  const attributedAny = attributed as any;
  const attributedAddress: string =
    typeof attributed === "string"
      ? attributed
      : (attributedAny?.address ?? String(attributed));

  const role = roleFromEvent(attributedAddress, event.eventType);
  const style = ROLE_STYLES[role];
  const source = parseThreadSource(attributedAddress);
  const collapsible = isCollapsibleByDefault(event.eventType, role);
  const [expanded, setExpanded] = useState(!collapsible);
  const [showMeta, setShowMeta] = useState(false);

  const timestamp = new Date(event.timestamp);
  // Show the message body for all MessageReceived events so the thread
  // reads as a real conversation rather than a list of envelope summaries.
  const bodyText = isMessageReceived && event.body ? event.body : null;

  // Display name resolution:
  //  - If the attributed value is a ParticipantRef with a non-empty
  //    displayName, use it.
  //  - Otherwise for non-human roles fall back to the address path so
  //    the source is still identifiable.
  //  - For human roles never fall back to the raw address (could be a
  //    UUID) — return null instead so the caller can omit the name.
  const resolvedDisplayName: string | null = (() => {
    const dn: string | null | undefined =
      typeof attributed === "string" ? null : attributedAny?.displayName;
    if (dn) return dn;
    if (role === "human") return null;
    // Non-human fallback: address path (e.g. "ada" from "agent://ada")
    return source.path || null;
  })();

  // #1161: error events render with destructive styling and are never
  // collapsed — the user cannot be expected to open the activity log to
  // discover a dispatch failure that happened inside their active
  // conversation. The alert-triangle icon signals the error visually
  // without relying solely on colour (WCAG 1.4.1).
  const isError = isErrorEvent(event.eventType, event.severity ?? "");

  const metaToggleButton =
    actions === "metadata" ? (
      <button
        type="button"
        onClick={() => setShowMeta((v) => !v)}
        aria-label={showMeta ? "Hide metadata" : "Show metadata"}
        aria-pressed={showMeta}
        data-testid={`${testIdPrefix}-meta-toggle`}
        className={cn(
          "rounded p-0.5 transition-colors hover:bg-accent focus-visible:outline-none focus-visible:ring-1 focus-visible:ring-ring",
          showMeta
            ? "text-primary"
            : "text-muted-foreground/50 hover:text-muted-foreground",
        )}
      >
        <Info className="h-3 w-3" aria-hidden="true" />
      </button>
    ) : null;

  const metaPanel =
    actions === "metadata" && showMeta ? (
      <div
        className="rounded border border-border bg-muted/40 p-2 text-[10px] font-mono text-muted-foreground space-y-0.5"
        data-testid={`${testIdPrefix}-meta-${event.id}`}
      >
        <p>
          <span className="text-foreground">id</span> {event.id}
        </p>
        <p>
          <span className="text-foreground">type</span> {event.eventType}
        </p>
        <p>
          <span className="text-foreground">source</span> {source.raw}
        </p>
        <p>
          <span className="text-foreground">severity</span> {event.severity}
        </p>
        {event.summary && (
          <p>
            <span className="text-foreground">summary</span> {event.summary}
          </p>
        )}
      </div>
    ) : null;

  const activityLinkFooter =
    actions === "activity-link" ? (
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
    ) : null;

  if (isError) {
    return (
      <div
        className="flex w-full justify-start"
        data-testid={`${testIdPrefix}-${event.id}`}
        data-role="error"
      >
        <div className="flex max-w-[80%] min-w-0 flex-col gap-1">
          <div className="flex items-center gap-2 text-xs text-muted-foreground">
            <Badge variant="destructive" className="h-5 px-1.5 text-[10px]">
              Error
            </Badge>
            {resolvedDisplayName && (
              <span
                className="truncate font-medium text-foreground/80"
                data-testid={`${testIdPrefix}-source-name`}
              >
                {resolvedDisplayName}
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
            {metaToggleButton}
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
          {metaPanel}
          {activityLinkFooter}
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
      data-testid={`${testIdPrefix}-${event.id}`}
      data-role={role}
    >
      <div className={cn("flex max-w-[80%] min-w-0 flex-col gap-1")}>
        {/* Header row: omit the role badge for human bubbles (#1502 Fix 1).
            Show displayName only when non-empty (never fall back to UUID). */}
        <div
          className={cn(
            "flex items-center gap-2 text-xs text-muted-foreground",
            style.align === "end" ? "justify-end" : "justify-start",
          )}
        >
          {role !== "human" && (
            <Badge variant="outline" className="h-5 px-1.5 text-[10px]">
              {style.label}
            </Badge>
          )}
          {resolvedDisplayName && (
            <span
              className="truncate font-medium text-foreground/80"
              data-testid={`${testIdPrefix}-source-name`}
            >
              {resolvedDisplayName}
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
          {metaToggleButton}
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

        {metaPanel}
        {activityLinkFooter}
      </div>
    </div>
  );
}
