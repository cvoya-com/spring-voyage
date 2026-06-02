"use client";

// Single-source-of-truth lifecycle badge for units and agents (#2372).
//
// Renders the same {Draft, Validating, Stopped, Starting, Running, Stopping,
// Error, Unknown} vocabulary that the backend's shared `LifecycleStatus` enum
// emits (#2364, #3006). `Unknown` is the read-time degraded indicator the API
// returns when an actor-state read fails or is canceled. The badge variant +
// dot colour mirror the rules previously inlined in `unit-card.tsx`, extended
// to cover the full state set so the agent card / list / detail header can
// drop the legacy "enabled ? running : stopped" projection.
//
// Accepts both PascalCase wire values (`UnitResponse.status`,
// `AgentResponse.lifecycleStatus`) and lowercase tree values
// (`NodeStatus` — `"running"` / `"draft"` / ...) so the same component
// works for the tree-driven Explorer header and the API-driven cards.

import { Badge } from "@/components/ui/badge";
import type { LifecycleStatus } from "@/lib/api/types";
import { cn } from "@/lib/utils";

export type LifecycleStatusInput = LifecycleStatus | string | null | undefined;

const STATUS_VARIANT: Record<
  LifecycleStatus,
  "default" | "success" | "warning" | "destructive" | "secondary" | "outline"
> = {
  Draft: "outline",
  Validating: "warning",
  Stopped: "secondary",
  Starting: "default",
  Running: "success",
  Stopping: "warning",
  Error: "destructive",
  Unknown: "warning",
};

const STATUS_DOT: Record<LifecycleStatus, string> = {
  Draft: "bg-muted-foreground",
  Validating: "bg-warning",
  Stopped: "bg-muted-foreground",
  Starting: "bg-warning",
  Running: "bg-success",
  Stopping: "bg-warning",
  Error: "bg-destructive",
  Unknown: "bg-warning",
};

const KNOWN: readonly LifecycleStatus[] = [
  "Draft",
  "Validating",
  "Stopped",
  "Starting",
  "Running",
  "Stopping",
  "Error",
  "Unknown",
];

/**
 * Normalise the wire value to PascalCase. The tree carries lowercase
 * (`"running"`), API responses carry PascalCase (`"Running"`). A null /
 * unrecognised value collapses to `"Unknown"` (#3006) — an honest degraded
 * indicator rather than masquerading as `"Draft"` — so the badge still
 * renders rather than blowing up on a contract drift.
 */
export function normaliseLifecycleStatus(
  input: LifecycleStatusInput,
): LifecycleStatus {
  if (!input) return "Unknown";
  const normalised = (input.charAt(0).toUpperCase() +
    input.slice(1).toLowerCase()) as LifecycleStatus;
  return KNOWN.includes(normalised) ? normalised : "Unknown";
}

interface LifecycleStatusBadgeProps {
  status: LifecycleStatusInput;
  /** When true (default), renders a leading coloured dot. */
  showDot?: boolean;
  className?: string;
  /** Optional test-id override. Defaults to `"lifecycle-status-badge"`. */
  testId?: string;
}

/**
 * Pill rendering the lifecycle status. The dot mirrors the badge
 * colour and is reused by the tree row + detail-pane header as a
 * standalone glyph via {@link LifecycleStatusDot}.
 */
export function LifecycleStatusBadge({
  status,
  showDot = true,
  className,
  testId,
}: LifecycleStatusBadgeProps) {
  const normalised = normaliseLifecycleStatus(status);
  return (
    <Badge
      variant={STATUS_VARIANT[normalised]}
      className={cn("gap-1", className)}
      data-testid={testId ?? "lifecycle-status-badge"}
      data-lifecycle-status={normalised}
    >
      {showDot && (
        <span
          aria-hidden="true"
          className={cn("h-1.5 w-1.5 shrink-0 rounded-full", STATUS_DOT[normalised])}
        />
      )}
      {normalised}
    </Badge>
  );
}

interface LifecycleStatusDotProps {
  status: LifecycleStatusInput;
  className?: string;
  testId?: string;
}

/**
 * Standalone coloured dot — same palette as {@link LifecycleStatusBadge}.
 * Used in tree rows + detail-pane headers where the badge would compete
 * with the lifecycle Badge that already carries the label.
 */
export function LifecycleStatusDot({
  status,
  className,
  testId,
}: LifecycleStatusDotProps) {
  const normalised = normaliseLifecycleStatus(status);
  return (
    <span
      aria-hidden="true"
      data-testid={testId}
      data-lifecycle-status={normalised}
      className={cn(
        "inline-block h-2.5 w-2.5 shrink-0 rounded-full",
        STATUS_DOT[normalised],
        className,
      )}
    />
  );
}
