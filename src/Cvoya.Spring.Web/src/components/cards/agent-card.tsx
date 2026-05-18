"use client";

import { Badge } from "@/components/ui/badge";
import { Card, CardContent } from "@/components/ui/card";
import {
  LifecycleStatusBadge,
  type LifecycleStatusInput,
} from "@/components/lifecycle-status-badge";
import { RuntimeStatusBadge } from "@/components/runtime-status-badge";
import type {
  AgentDashboardSummary,
  AgentExecutionMode,
} from "@/lib/api/types";
import { cn, timeAgo } from "@/lib/utils";
import {
  Clock,
  DollarSign,
  ExternalLink,
  Layers,
  MessagesSquare,
} from "lucide-react";
import Link from "next/link";

import { CardTabRow, type CardTabName } from "./card-tab-row";

/**
 * Minimal shape that the AgentCard needs. `AgentDashboardSummary` from
 * the dashboard endpoint satisfies this; the extra optional fields allow
 * callers from richer endpoints (agent detail, unit agents tab) to pass
 * through parent unit, execution mode, status and last-activity text.
 */
export interface AgentCardAgent {
  name: string;
  displayName: string;
  role?: string | null;
  registeredAt: string;
  parentUnit?: string | null;
  /**
   * Full 7-state lifecycle status (#2372). Mirrors
   * {@link import("@/lib/api/types").LifecycleStatus} — `Draft`,
   * `Validating`, `Stopped`, `Starting`, `Running`, `Stopping`, `Error`.
   * Lowercase tree-side values (`"running"` / `"draft"`) are also
   * accepted by the badge so callers reading from the tenant tree don't
   * need to re-normalise.
   */
  status?: LifecycleStatusInput;
  executionMode?: AgentExecutionMode | null;
  /** Short one-line summary of the most recent activity, if known. */
  lastActivity?: string | null;
  /**
   * Stable Guid of the agent — when present, the card renders the
   * runtime-status indicator chip (#2100). Optional because the
   * dashboard-summary shape doesn't carry it; that surface degrades to
   * the legacy badge-only rendering when omitted.
   */
  id?: string | null;
}

/**
 * Tab chips rendered in the `<CardTabRow>` footer when `onOpenTab` is
 * provided. Matches plan §7 of the v2 design-system rollout (#815):
 * deeplinks for the Explorer's Agent tab catalog, excluding Overview
 * (already covered by the card's primary "Open" affordance).
 */
export const AGENT_CARD_TABS: readonly CardTabName[] = [
  "Messages",
  "Activity",
  "Memory",
  "Skills",
  "Traces",
  "Clones",
  "Config",
] as const;

interface AgentCardProps {
  agent: AgentCardAgent | AgentDashboardSummary;
  /** Parent-unit override, when known from the caller's context. */
  parentUnit?: string | null;
  /** Override for the most recent activity summary. */
  lastActivity?: string | null;
  /**
   * Optional contextual quick-actions rendered next to the "Open" affordance
   * in the card footer. Used by the unit membership editor (#472) to expose
   * Edit / Remove without breaking the shared `<AgentCard>` layout. Renders
   * `null` when omitted, so dashboard / list usages stay unchanged.
   */
  actions?: React.ReactNode;
  /**
   * When provided, the card renders a `<CardTabRow>` footer of
   * icon-only tab-deeplink chips (plan §7). Clicking a chip dispatches
   * `(agent.name, tabName)` — callers wire the handler to the Explorer
   * selection bridge (`<ExplorerSelectionProvider>`) or to their own
   * router.push. Legacy cross-link icons (Conversations / Cost) render
   * as the fallback when this prop is omitted so existing call sites
   * keep working unchanged.
   */
  onOpenTab?: (agentName: string, tab: CardTabName) => void;
  /**
   * When provided, the card's primary click (overlay + "Open" link) calls
   * this instead of navigating via Next.js `<Link>`. Use inside the
   * Explorer (#2464) so clicking an agent member card dispatches selection
   * through the in-memory bridge rather than triggering an App Router
   * same-route RSC navigation — the navigation kicks off a React
   * transition that pins the visible state until it settles, so the
   * first click "highlights but does not navigate". Mirrors the
   * matching `onSelect` opt-in on `<UnitCard>` from PR #2390.
   */
  onSelect?: (agentName: string) => void;
  className?: string;
}

/**
 * Reusable agent card primitive. See plan §7 of the v2 design-system
 * rollout (#815): each agent card ends in a `<CardTabRow>` of icon-only
 * tab deeplinks that fire `onOpenTab(agent.name, tab)` into the
 * Explorer. Dashboard + Explorer usages wire the handler; list pages
 * that want the legacy cross-link icons omit the prop.
 */
export function AgentCard({
  agent,
  parentUnit,
  lastActivity,
  actions,
  onOpenTab,
  onSelect,
  className,
}: AgentCardProps) {
  // Post-`DEL-agents` (#870): the legacy `/agents/<name>` detail route
  // is gone. Agents render under their primary parent in the Explorer
  // tree, so the card's primary affordance deep-links into the canonical
  // `/explorer/units/<name>` path (#2473). Cross-link chips share the
  // same base and append `?tab=…`.
  const nodeParam = encodeURIComponent(agent.name.replace(/-/g, ""));
  const href = `/explorer/units/${nodeParam}?tab=Overview`;
  // Legacy cross-link chips (Conversations, Cost) render only when the
  // caller omits `onOpenTab` — Explorer usages always set it, so these
  // are fallback links. The Agent card's Overview tab carries the cost
  // summary post-v2, so the cost chip lands on Overview too.
  const conversationsHref = `/explorer/units/${nodeParam}?tab=Messages`;
  const costHref = `/explorer/units/${nodeParam}?tab=Overview`;
  const parent =
    parentUnit ?? ("parentUnit" in agent ? agent.parentUnit : undefined);
  const lastActivityText =
    lastActivity ?? ("lastActivity" in agent ? agent.lastActivity : undefined);
  const status: LifecycleStatusInput =
    "status" in agent ? agent.status ?? null : null;
  const execMode =
    "executionMode" in agent ? agent.executionMode ?? null : null;
  // #2100: render the runtime-status indicator next to the role/status
  // pills when the caller threads through the agent's stable id. The
  // dashboard summary shape doesn't carry it; the card degrades to the
  // legacy badge-only layout in that case.
  const runtimeId = "id" in agent ? agent.id ?? null : null;

  return (
    <Card
      data-testid={`agent-card-${agent.name}`}
      className={cn(
        "relative h-full overflow-hidden transition-colors hover:border-primary/50 hover:bg-muted/30 focus-within:ring-2 focus-within:ring-ring focus-within:ring-offset-2",
        className,
      )}
    >
      <CardContent className="p-4">
        {/*
          Full-card overlay link (#593). The primary card link expands to
          cover the whole card via the `::after` pseudo-element so every
          unused surface area navigates to the agent detail page on click.
          All other interactive descendants are promoted to `relative z-[1]`
          so they sit above the overlay and keep their own click targets.
          Tab focus still lands on this link first; Enter activates it,
          matching `role="link"` keyboard semantics.
        */}
        <Link
          href={href}
          aria-label={`Open agent ${agent.displayName}`}
          data-testid={`agent-card-link-${agent.name}`}
          onClick={onSelect ? (e) => { e.preventDefault(); onSelect(agent.name); } : undefined}
          className="flex items-start justify-between gap-2 rounded-sm focus-visible:outline-none after:absolute after:inset-0 after:content-['']"
        >
          <div className="min-w-0 flex-1">
            <h3 className="truncate font-semibold">{agent.displayName}</h3>
            <p className="mt-0.5 text-xs text-muted-foreground">{agent.name}</p>
          </div>
          <div className="relative z-[1] flex shrink-0 items-center gap-2">
            {agent.role && (
              <Badge variant="secondary" data-testid="agent-role-badge">
                {agent.role}
              </Badge>
            )}
            {status && (
              <LifecycleStatusBadge
                status={status}
                showDot={false}
                testId="agent-status-badge"
              />
            )}
            {execMode && (
              <Badge variant="outline" data-testid="agent-execution-mode-badge">
                {execMode}
              </Badge>
            )}
            {runtimeId && (
              <RuntimeStatusBadge
                kind="agent"
                id={runtimeId}
                testId={`agent-runtime-status-${agent.name}`}
              />
            )}
          </div>
        </Link>

        {/*
          Meta row (parent unit + registered-at). `pointer-events-none`
          lets clicks on the row's whitespace fall through to the
          full-card overlay link above; the parent-unit `<Link>` restores
          `pointer-events-auto` so its own click target survives.
          Mirrors the footer-strip fix from PR #2390 — same pattern,
          every non-overlay row (#2441).
        */}
        <div className="pointer-events-none mt-3 flex flex-wrap items-center gap-2 text-xs text-muted-foreground">
          {parent && (
            <Link
              href={`/explorer/units/${encodeURIComponent(parent.replace(/-/g, ""))}`}
              data-testid="agent-parent-unit"
              aria-label={`Open parent unit ${parent}`}
              className="pointer-events-auto relative z-[1] flex items-center gap-1 rounded-sm transition-colors hover:text-foreground"
            >
              <Layers className="h-3 w-3" aria-hidden="true" />
              {parent}
            </Link>
          )}
          <span className="flex items-center gap-1">
            <Clock className="h-3 w-3" aria-hidden="true" />
            {timeAgo(agent.registeredAt)}
          </span>
        </div>

        {lastActivityText && (
          <p
            className="pointer-events-none mt-2 truncate text-xs italic text-muted-foreground"
            data-testid="agent-last-activity"
          >
            {lastActivityText}
          </p>
        )}

        {/*
          Footer strip stays `relative z-[1]` so interactive children paint
          above the full-card overlay, but `pointer-events-none` lets the
          gap between the cross-link icons and the right-hand action group
          fall through to the overlay link — clicking that whitespace now
          navigates to the agent instead of dying on a wrapper div.
          Interactive descendants restore `pointer-events-auto`.
        */}
        <div className="pointer-events-none relative z-[1] mt-3 flex items-center justify-between">
          {/* Legacy cross-links: suppressed when the `<CardTabRow>`
              footer is active via `onOpenTab`. */}
          <div
            className="flex items-center gap-1"
            data-testid={`agent-cross-links-${agent.name}`}
          >
            {onOpenTab ? null : (
              <>
                <CrossLinkButton
                  href={conversationsHref}
                  label={`View conversations for ${agent.displayName}`}
                  icon={<MessagesSquare className="h-3.5 w-3.5" />}
                  testId={`agent-link-conversations-${agent.name}`}
                />
                <CrossLinkButton
                  href={costHref}
                  label={`View cost detail for ${agent.displayName}`}
                  icon={<DollarSign className="h-3.5 w-3.5" />}
                  testId={`agent-link-cost-${agent.name}`}
                />
              </>
            )}
          </div>
          <div className="flex items-center gap-1">
            {actions && (
              <div
                className="pointer-events-auto flex items-center gap-1"
                data-testid={`agent-actions-${agent.name}`}
              >
                {actions}
              </div>
            )}
            <Link
              href={href}
              onClick={onSelect ? (e) => { e.preventDefault(); onSelect(agent.name); } : undefined}
              className="pointer-events-auto inline-flex items-center gap-1 rounded-md px-2 py-1 text-xs text-primary hover:underline"
              data-testid={`agent-open-${agent.name}`}
            >
              Open
              <ExternalLink className="h-3 w-3" aria-hidden="true" />
            </Link>
          </div>
        </div>
      </CardContent>
      {onOpenTab && (
        <div
          className="relative z-[1]"
          data-testid={`agent-card-tabrow-${agent.name}`}
        >
          <CardTabRow
            id={agent.name}
            tabs={AGENT_CARD_TABS}
            onOpenTab={(id, tab) => onOpenTab(id, tab)}
          />
        </div>
      )}
    </Card>
  );
}

function CrossLinkButton({
  href,
  label,
  icon,
  testId,
}: {
  href: string;
  label: string;
  icon: React.ReactNode;
  testId: string;
}) {
  return (
    <Link
      href={href}
      aria-label={label}
      title={label}
      data-testid={testId}
      className="pointer-events-auto inline-flex h-7 w-7 items-center justify-center rounded-md text-muted-foreground transition-colors hover:bg-accent hover:text-foreground"
    >
      {icon}
    </Link>
  );
}
