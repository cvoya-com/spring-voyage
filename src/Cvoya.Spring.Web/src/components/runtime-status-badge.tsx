"use client";

// Runtime-status indicator component (#2100).
//
// Renders a small, accessible chip next to an agent or unit name across
// every portal surface that surfaces them: engagement timeline header
// and bubbles, agent / unit cards, member rosters in unit views, and
// mention chips in compose surfaces. The chip:
//
//  - polls the dedicated `runtime-status` endpoint at ~2s cadence (see
//    `use-runtime-status.ts`); the chip surfaces the previous state on
//    transient failures and the literal `unknown` slot only on the first
//    failed poll.
//  - encodes status with both an icon and a text label so colour-blind
//    operators are not asked to resolve "yellow vs green" (WCAG 1.4.1).
//  - supports a compact variant (`size="dot"`) for chrome-tight surfaces
//    like the engagement-timeline bubble header — `size="default"` is
//    used in cards / drawer panels where extra horizontal real estate
//    is available.
//  - dark-mode safe via design-tokens (success / warning / destructive
//    semantic palette per DESIGN.md § 2.5).

import { useMemo } from "react";
import {
  CheckCircle2,
  CircleDashed,
  CircleSlash2,
  Loader2,
  PauseCircle,
} from "lucide-react";

import {
  projectStatus,
  useRuntimeStatus,
  type RuntimeStatusKind,
} from "@/lib/api/use-runtime-status";
import type { RuntimeStatus } from "@/lib/api/types";
import { cn } from "@/lib/utils";

export type RuntimeStatusBadgeSize = "default" | "dot";

export interface RuntimeStatusBadgeProps {
  /** Whether the indicator targets an agent or a unit (#2100 — units are agents per ADR-0017). */
  kind: RuntimeStatusKind;
  /** Stable Guid of the actor; the chip is hidden when empty / null. */
  id: string | null | undefined;
  /**
   * Display variant. `"default"` renders an icon + label pill; `"dot"`
   * renders the icon-only chrome-tight variant used in tight surfaces
   * like message-bubble headers and member-list rows. Both expose the
   * status to assistive tech via `aria-label`.
   */
  size?: RuntimeStatusBadgeSize;
  /**
   * Suspend polling when the surrounding region is hidden / off-screen.
   * Default `true`. Drawer-panel teardown sets `false` so the polling
   * task does not outlive the panel.
   */
  enabled?: boolean;
  /** Optional className passthrough for layout overrides. */
  className?: string;
  /** Optional test-id override for E2E assertions. */
  testId?: string;
}

interface PresentationSpec {
  label: string;
  /** ARIA description so screen-reader users get the full picture. */
  ariaDescription: string;
  containerClass: string;
  dotClass: string;
  /** Lucide icon component. Sized by the surrounding container. */
  Icon: typeof CheckCircle2;
}

const SPECS: Record<RuntimeStatus, PresentationSpec> = {
  idle: {
    label: "Idle",
    ariaDescription: "Idle — ready to pick up the next message.",
    containerClass: "border-border bg-muted/40 text-muted-foreground",
    dotClass: "bg-muted-foreground/40",
    Icon: CheckCircle2,
  },
  busy: {
    label: "Busy",
    ariaDescription:
      "Busy — currently processing a message (may be on another thread).",
    // Per DESIGN.md § 2.5 the warning palette is reserved for "in-flight
    // / attention" semantics; busy is the canonical match.
    containerClass: "border-warning/50 bg-warning/10 text-warning",
    dotClass: "bg-warning",
    Icon: Loader2,
  },
  queued: {
    label: "Queued",
    ariaDescription:
      "Queued — messages are waiting behind in-flight work on this actor.",
    containerClass: "border-primary/40 bg-primary/10 text-primary",
    dotClass: "bg-primary",
    Icon: PauseCircle,
  },
  unavailable: {
    label: "Unavailable",
    ariaDescription:
      "Unavailable — the actor's container is not running or has failed health checks.",
    containerClass: "border-destructive/50 bg-destructive/10 text-destructive",
    dotClass: "bg-destructive",
    Icon: CircleSlash2,
  },
  unknown: {
    label: "Status loading…",
    ariaDescription: "Status loading — first poll in flight.",
    containerClass: "border-border bg-muted/40 text-muted-foreground",
    dotClass: "bg-muted-foreground/30",
    Icon: CircleDashed,
  },
};

/**
 * Status indicator chip rendered next to every agent/unit name. See the
 * file-level comment for design rationale (#2100).
 */
export function RuntimeStatusBadge({
  kind,
  id,
  size = "default",
  enabled = true,
  className,
  testId,
}: RuntimeStatusBadgeProps) {
  const query = useRuntimeStatus(kind, id, { enabled });
  const status = useMemo(
    () => projectStatus(query.data?.status),
    [query.data?.status],
  );
  const spec = SPECS[status];

  // Hide entirely when the caller has no id — the chip should never
  // render an empty placeholder for a name we don't know how to reach.
  if (!id) {
    return null;
  }

  const tooltipParts: string[] = [spec.ariaDescription];
  if (query.data) {
    if (query.data.inFlightThreadCount > 0) {
      tooltipParts.push(
        `In-flight on ${query.data.inFlightThreadCount} thread${query.data.inFlightThreadCount === 1 ? "" : "s"}.`,
      );
    }
    if (query.data.queuedMessageCount > 0) {
      tooltipParts.push(
        `${query.data.queuedMessageCount} message${query.data.queuedMessageCount === 1 ? "" : "s"} queued.`,
      );
    }
  }
  const tooltip = tooltipParts.join(" ");

  if (size === "dot") {
    return (
      <span
        role="status"
        aria-label={spec.ariaDescription}
        title={tooltip}
        data-testid={testId ?? `runtime-status-${kind}`}
        data-runtime-status={status}
        className={cn(
          "inline-flex h-2 w-2 shrink-0 rounded-full",
          spec.dotClass,
          status === "busy" && "animate-pulse",
          className,
        )}
      />
    );
  }

  return (
    <span
      role="status"
      aria-label={spec.ariaDescription}
      title={tooltip}
      data-testid={testId ?? `runtime-status-${kind}`}
      data-runtime-status={status}
      className={cn(
        "inline-flex items-center gap-1 rounded-full border px-2 py-0.5 text-[10px] font-medium leading-none",
        spec.containerClass,
        className,
      )}
    >
      <spec.Icon
        aria-hidden="true"
        className={cn(
          "h-3 w-3 shrink-0",
          status === "busy" && "animate-spin",
        )}
      />
      <span>{spec.label}</span>
    </span>
  );
}
