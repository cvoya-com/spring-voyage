"use client";

import type { ReactNode } from "react";

import { cn } from "@/lib/utils";

import type { NodeKind, TabName } from "./aggregate";

interface TabPlaceholderProps {
  /** The tab whose content is missing — surfaced verbatim in the empty-state copy. */
  tab: TabName;
  /** The kind of node the tab belongs to. Drives the placeholder's affordance copy. */
  kind: NodeKind;
  /**
   * Optional override body. A tab that needs a reason-aware placeholder
   * ("Memory write API ships in v2.1") can register a stub component that
   * wraps this chrome with a hand-written body.
   */
  children?: ReactNode;
  className?: string;
}

/**
 * Generic empty-state shown when the tab registry has no entry for the
 * active `(kind, tab)` pair.
 *
 * Operators should not see this copy in production — every visible tab has
 * a registered component before merge. The component is exported so stub
 * tabs that are waiting on a backend (memory write, traces API, …) can
 * re-use the chrome with a hand-written body.
 */
export function TabPlaceholder({
  tab,
  kind,
  children,
  className,
}: TabPlaceholderProps) {
  return (
    <div
      role="status"
      aria-live="polite"
      data-testid={`tab-placeholder-${tab.toLowerCase()}`}
      data-tab={tab}
      data-kind={kind}
      className={cn(
        "rounded-lg border border-dashed border-border bg-muted/30 p-8 text-center",
        className,
      )}
    >
      <p className="text-sm font-medium text-foreground">
        {tab} tab — coming soon
      </p>
      <p className="mt-1 text-xs text-muted-foreground">
        {children ?? `The ${tab} surface for ${kindLabel(kind)} nodes is wired up by an upcoming Explorer issue.`}
      </p>
    </div>
  );
}

function kindLabel(kind: NodeKind): string {
  switch (kind) {
    case "Tenant":
      return "tenant";
    case "Unit":
      return "unit";
    case "Agent":
      return "agent";
  }
}
