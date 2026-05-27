"use client";

// Detail popover surfaced when an edge or a live pulse is clicked
// (#2867). Renders sender, recipient, timestamp, channel, and a link
// into the per-thread conversation view. Kept deliberately small — the
// page lays this out as a side sheet so the graph stays visible.

import Link from "next/link";
import { ExternalLink, X } from "lucide-react";

import { Badge } from "@/components/ui/badge";
import { Button } from "@/components/ui/button";
import { Card, CardContent } from "@/components/ui/card";
import { cn } from "@/lib/utils";

export interface InteractionDetailSubject {
  /** Sender node id (canonical no-dash guid). */
  fromId: string;
  /** Sender display name. */
  fromName: string;
  /** Sender kind for the colour swatch. */
  fromKind: string;
  /** Receiver node id. */
  toId: string;
  /** Receiver display name. */
  toName: string;
  /** Receiver kind. */
  toKind: string;
  /** Aggregate message count on this edge. */
  count: number;
  /** Channels (recipient schemes) observed for this edge. */
  channels: readonly string[];
  /** Most recent timestamp on this edge. */
  lastAt: string;
  /** First timestamp on this edge. */
  firstAt: string;
  /** Optional thread id surfaced by a single pulse click. */
  threadId?: string | null;
}

interface InteractionDetailProps {
  subject: InteractionDetailSubject;
  onClose: () => void;
}

export function InteractionDetail({ subject, onClose }: InteractionDetailProps) {
  return (
    <Card
      role="dialog"
      aria-label="Interaction detail"
      data-testid="interaction-detail"
      className="w-80 shrink-0"
    >
      <CardContent className="space-y-3 p-4 text-sm">
        <div className="flex items-start justify-between gap-2">
          <div className="space-y-1">
            <p className="text-xs uppercase tracking-wider text-muted-foreground">
              Interaction
            </p>
            <p
              className="font-medium"
              data-testid="interaction-detail-title"
            >
              {subject.fromName} → {subject.toName}
            </p>
          </div>
          <Button
            variant="ghost"
            size="sm"
            onClick={onClose}
            aria-label="Close detail"
            data-testid="interaction-detail-close"
            className="h-7 w-7 p-0"
          >
            <X className="h-3.5 w-3.5" aria-hidden="true" />
          </Button>
        </div>

        <div className="grid grid-cols-2 gap-2 text-xs">
          <div className="space-y-0.5">
            <p className="text-muted-foreground">Sender</p>
            <p className="font-mono truncate" title={subject.fromId}>
              {subject.fromName}
            </p>
            <Badge
              variant="outline"
              className={cn("text-[10px]", kindBadgeClass(subject.fromKind))}
            >
              {subject.fromKind}
            </Badge>
          </div>
          <div className="space-y-0.5">
            <p className="text-muted-foreground">Receiver</p>
            <p className="font-mono truncate" title={subject.toId}>
              {subject.toName}
            </p>
            <Badge
              variant="outline"
              className={cn("text-[10px]", kindBadgeClass(subject.toKind))}
            >
              {subject.toKind}
            </Badge>
          </div>
        </div>

        <div className="space-y-1 text-xs">
          <div className="flex justify-between">
            <span className="text-muted-foreground">Messages</span>
            <span
              className="font-mono tabular-nums"
              data-testid="interaction-detail-count"
            >
              {subject.count}
            </span>
          </div>
          <div className="flex justify-between">
            <span className="text-muted-foreground">First seen</span>
            <span className="font-mono tabular-nums">
              {new Date(subject.firstAt).toLocaleString()}
            </span>
          </div>
          <div className="flex justify-between">
            <span className="text-muted-foreground">Last seen</span>
            <span className="font-mono tabular-nums">
              {new Date(subject.lastAt).toLocaleString()}
            </span>
          </div>
          <div className="flex justify-between gap-2">
            <span className="text-muted-foreground">Channels</span>
            <span
              className="font-mono text-right"
              data-testid="interaction-detail-channels"
            >
              {subject.channels.length === 0
                ? "—"
                : subject.channels.join(", ")}
            </span>
          </div>
        </div>

        {subject.threadId ? (
          <Link
            href={`/conversations?thread=${encodeURIComponent(subject.threadId)}`}
            className="inline-flex items-center gap-1 rounded border border-input bg-background px-2 py-1 text-xs text-primary hover:bg-accent"
            data-testid="interaction-detail-thread-link"
          >
            <ExternalLink className="h-3 w-3" aria-hidden="true" />
            Open conversation
          </Link>
        ) : null}
      </CardContent>
    </Card>
  );
}

/**
 * Per-kind tint for the node-kind badge. Each kind is grounded in an
 * existing semantic colour token so the palette stays consistent with
 * the design system (DESIGN.md § 4).
 */
function kindBadgeClass(kind: string): string {
  switch (kind) {
    case "agent":
      // Primary blue — agents are the canonical actor type in the system.
      return "border-primary/40 text-primary";
    case "unit":
      // Voyage cyan — units carry the brand-extension accent because
      // they are the "team" / scope abstraction across the portal.
      return "border-voyage/40 text-voyage";
    case "human":
      // Blossom pink — humans are the only first-class non-agent actor.
      return "border-blossom-deep/40 text-blossom-deep";
    case "connector":
      // Warning gold — connectors are source-only and visually distinct
      // from the autonomous actors above.
      return "border-warning/40 text-warning";
    default:
      return "border-border text-muted-foreground";
  }
}

export const NODE_KIND_TINT = {
  agent: "var(--color-primary)",
  unit: "var(--color-voyage)",
  human: "var(--color-blossom-deep)",
  connector: "var(--color-warning)",
  default: "var(--color-muted-foreground)",
} as const;
