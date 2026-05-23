"use client";

import { useState } from "react";
import ReactMarkdown from "react-markdown";
import remarkGfm from "remark-gfm";
import {
  AlertTriangle,
  ChevronDown,
  ChevronRight,
  Info,
  Wrench,
} from "lucide-react";
import Link from "next/link";

import { Badge } from "@/components/ui/badge";
import { RuntimeStatusBadge } from "@/components/runtime-status-badge";
import { cn } from "@/lib/utils";
import type { ThreadEvent } from "@/lib/api/types";

import {
  addressOf,
  buildParticipantNameResolver,
  idOf,
  parseThreadSource,
  participantDisplayName,
  renderBodyWithResolvedAddresses,
  ROLE_STYLES,
  roleFromEvent,
  runtimeKindOf,
  splitBodyIntoStructuredAndProse,
  type AddressLike,
  type ConversationRole,
} from "./role";
import { StructuredPayloadCard } from "./structured-payload-card";

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
  /**
   * Override the row alignment. By default the row aligns by role
   * (human → end / right; everyone else → start / left). Pass
   * `"start"` to force-left-justify the row regardless of role —
   * used by the engagement observer-view timeline layout (#1630)
   * where the dialog metaphor is wrong.
   */
  align?: "auto" | "start";
  /**
   * Thread-level participants (#2089). Used to resolve raw
   * `scheme://<guid>` / `scheme:<guid>` addresses that a noisy LLM may
   * leak into a `MessageArrived` body — they fold down to the
   * participant's display name in the rendered bubble. The event's own
   * `source` / `from` / `to` are always merged into the lookup, so the
   * sender's name resolves even on surfaces that don't pass the full
   * thread participants list.
   */
  participants?: ReadonlyArray<AddressLike>;
}

/**
 * One row in the conversation thread view. Renders the event as a
 * chat-style bubble, aligned left/right by role (human → right, every
 * other role → left). Tool/lifecycle events start collapsed; text
 * messages stay expanded.
 *
 * For `MessageArrived` events the bubble represents the *sender* of the
 * underlying message — i.e. `event.from` — not `event.source` (which is
 * the receiving actor that emitted the event). Without this distinction
 * a message from agent://qa-engineer to a human would render as a
 * right-aligned "human-sent" bubble because the human's actor projected
 * the receive event. For all other event types, source is the canonical
 * attribution.
 *
 * If the event carries a message body, render the body in place of the
 * envelope summary so the thread reads as a real conversation rather
 * than a list of platform-side summary placeholders.
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
  align = "auto",
  participants,
}: ThreadEventRowProps) {
  // Attribute MessageArrived bubbles to the sender (event.from) rather
  // than the receiver-projected event.source.
  const isMessageArrived = event.eventType === "MessageArrived";
  const attributed =
    isMessageArrived && event.from ? event.from : event.source;

  // `attributed` may be a ParticipantRef object (address + displayName)
  // or a plain address string when served by an older API version.
  const attributedAddress = addressOf(attributed) || String(attributed);

  const role = roleFromEvent(attributedAddress, event.eventType);
  const style = ROLE_STYLES[role];
  // Effective alignment: callers in observer-view timelines force-start
  // so the dialog metaphor doesn't leak into surfaces where there is no
  // active-user axis (#1630).
  const effectiveAlign: "start" | "end" =
    align === "start" ? "start" : style.align;
  const source = parseThreadSource(attributedAddress);
  const collapsible = isCollapsibleByDefault(event.eventType, role);
  const [expanded, setExpanded] = useState(!collapsible);
  const [showMeta, setShowMeta] = useState(false);

  const timestamp = new Date(event.timestamp);
  // Show the message body for all MessageArrived events so the thread
  // reads as a real conversation rather than a list of envelope summaries.
  // Raw `scheme://<guid>` addresses leaked into the body by a noisy LLM
  // (#2089) are folded down to participant display names via the shared
  // resolver. The event's own attribution participants are always merged
  // into the lookup so the sender / recipient resolve even on surfaces
  // that don't pass the full thread participants list.
  //
  // #2128: a body that starts with a JSON object is split — the leading
  // envelope is folded into a collapsed structured-payload card below
  // the bubble, and only the trailing prose flows through the bubble
  // (still address-resolved). Detection is shape-only and the storage
  // shape is unchanged; this is purely a render-time transform.
  const rawBody = isMessageArrived && event.body ? event.body : null;
  const { structured, prose } = rawBody
    ? splitBodyIntoStructuredAndProse(rawBody)
    : { structured: null, prose: "" };
  const bodyText =
    rawBody === null
      ? null
      : renderBodyWithResolvedAddresses(
          prose,
          buildParticipantNameResolver([
            event.source,
            event.from,
            event.to,
            ...(participants ?? []),
          ]),
        );
  // When the body is a pure JSON envelope (no trailing prose) the bubble
  // has nothing to say — render only the structured card to avoid an
  // empty-paragraph artifact. The header (badge / name / timestamp) and
  // the card both stay in the same column so the row reads as one unit.
  const hasProseBubble = bodyText === null || bodyText !== "";

  // Display name resolution: shared helper passes through the
  // server-supplied `displayName` (#1635 / PR #1643 / #1645). When the
  // server cannot resolve a participant it returns the `<deleted>`
  // sentinel; raw GUIDs leaking here is a server-side resolver bug.
  const resolvedDisplayName = participantDisplayName(attributed);

  // Runtime-status indicator for the bubble header (#2100). Only rendered
  // when the event is attributed to an addressable agent or unit — humans
  // and tool / system rows do not carry one.
  const runtimeKind = runtimeKindOf(attributed);
  const runtimeId = idOf(attributed);

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
          effectiveAlign === "end" ? "justify-end" : "justify-start",
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
            {runtimeKind && runtimeId && (
              <RuntimeStatusBadge
                kind={runtimeKind}
                id={runtimeId}
                size="dot"
                testId={`${testIdPrefix}-source-status`}
              />
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
        effectiveAlign === "end" ? "justify-end" : "justify-start",
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
            effectiveAlign === "end" ? "justify-end" : "justify-start",
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
          {runtimeKind && runtimeId && (
            <RuntimeStatusBadge
              kind={runtimeKind}
              id={runtimeId}
              size="dot"
              testId={`${testIdPrefix}-source-status`}
            />
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

        {hasProseBubble && (
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
              <div className="prose prose-sm dark:prose-invert max-w-none break-words [&_a]:text-primary [&_a]:underline [&_code:not(pre_code)]:rounded [&_code:not(pre_code)]:bg-black/10 [&_code:not(pre_code)]:px-1 [&_code:not(pre_code)]:dark:bg-white/10 [&_pre]:overflow-x-auto [&_pre]:rounded [&_pre]:bg-black/10 [&_pre]:p-2 [&_pre]:dark:bg-white/10 [&_table]:w-full [&_table]:border-collapse [&_td]:border [&_td]:border-border [&_td]:px-2 [&_td]:py-1 [&_th]:border [&_th]:border-border [&_th]:px-2 [&_th]:py-1 [&_th]:text-left">
                <ReactMarkdown
                  remarkPlugins={[remarkGfm]}
                  components={{
                    a: ({ href, children }) => (
                      <a
                        href={href}
                        target="_blank"
                        rel="noopener noreferrer"
                      >
                        {children}
                      </a>
                    ),
                  }}
                >
                  {bodyText ?? event.summary ?? ""}
                </ReactMarkdown>
              </div>
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
        )}

        {structured && (
          <StructuredPayloadCard
            payload={structured}
            testIdPrefix={`${testIdPrefix}-${event.id}-payload`}
          />
        )}

        {metaPanel}
        {activityLinkFooter}
      </div>
    </div>
  );
}
