"use client";

import { Badge } from "@/components/ui/badge";
import { Card, CardContent } from "@/components/ui/card";
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
  status?: string | null;
  executionMode?: AgentExecutionMode | null;
  /** Short one-line summary of the most recent activity, if known. */
  lastActivity?: string | null;
}

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
  className?: string;
}

const statusVariant: Record<
  string,
  "default" | "success" | "warning" | "destructive" | "secondary" | "outline"
> = {
  idle: "secondary",
  active: "success",
  busy: "warning",
  error: "destructive",
};

/**
 * Reusable agent card primitive. See § 3.3 of the portal design doc:
 * every agent card links to its parent unit, its conversations, and its
 * cost detail.
 */
export function AgentCard({
  agent,
  parentUnit,
  lastActivity,
  actions,
  className,
}: AgentCardProps) {
  const href = `/agents/${encodeURIComponent(agent.name)}`;
  const conversationsHref = `${href}?tab=conversations`;
  const costHref = `${href}?tab=costs`;
  const parent =
    parentUnit ?? ("parentUnit" in agent ? agent.parentUnit : undefined);
  const lastActivityText =
    lastActivity ?? ("lastActivity" in agent ? agent.lastActivity : undefined);
  const status = "status" in agent ? agent.status ?? null : null;
  const execMode =
    "executionMode" in agent ? agent.executionMode ?? null : null;

  return (
    <Card
      data-testid={`agent-card-${agent.name}`}
      className={cn(
        "relative h-full transition-colors hover:border-primary/50 hover:bg-muted/30 focus-within:ring-2 focus-within:ring-ring focus-within:ring-offset-2",
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
              <Badge
                variant={statusVariant[status.toLowerCase()] ?? "outline"}
                data-testid="agent-status-badge"
              >
                {status}
              </Badge>
            )}
            {execMode && (
              <Badge variant="outline" data-testid="agent-execution-mode-badge">
                {execMode}
              </Badge>
            )}
          </div>
        </Link>

        <div className="mt-3 flex flex-wrap items-center gap-2 text-xs text-muted-foreground">
          {parent && (
            <Link
              href={`/units/${encodeURIComponent(parent)}`}
              data-testid="agent-parent-unit"
              aria-label={`Open parent unit ${parent}`}
              className="relative z-[1] flex items-center gap-1 rounded-sm transition-colors hover:text-foreground"
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
            className="mt-2 truncate text-xs italic text-muted-foreground"
            data-testid="agent-last-activity"
          >
            {lastActivityText}
          </p>
        )}

        <div className="relative z-[1] mt-3 flex items-center justify-between">
          <div className="flex items-center gap-1">
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
          </div>
          <div className="flex items-center gap-1">
            {actions && (
              <div
                className="flex items-center gap-1"
                data-testid={`agent-actions-${agent.name}`}
              >
                {actions}
              </div>
            )}
            <Link
              href={href}
              className="inline-flex items-center gap-1 rounded-md px-2 py-1 text-xs text-primary hover:underline"
              data-testid={`agent-open-${agent.name}`}
            >
              Open
              <ExternalLink className="h-3 w-3" aria-hidden="true" />
            </Link>
          </div>
        </div>
      </CardContent>
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
      className="inline-flex h-7 w-7 items-center justify-center rounded-md text-muted-foreground transition-colors hover:bg-accent hover:text-foreground"
    >
      {icon}
    </Link>
  );
}
