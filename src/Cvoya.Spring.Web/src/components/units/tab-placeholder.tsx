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
   * Optional override body. Tabs that ship in a future PR can register a
   * named, reason-aware placeholder ("Memory write API ships in v2.1") via
   * the registry while still leaning on this component's chrome.
   */
  children?: ReactNode;
  className?: string;
}

/**
 * Generic empty-state shown when the {@link tabsRegistry} has no entry
 * for the active `(kind, tab)` pair.
 *
 * Foundation issue `FOUND-tabscaffold` ships this component + an empty
 * registry; each `EXP-tab-*` issue then drops a one-line registration in
 * `tabs/index.ts` to replace this fallback. The placeholder's copy is
 * intentionally low-key — operators should never see it in production
 * because every visible tab has a registered component before merge.
 *
 * The component is exported standalone so EXP-tab-* issues that need to
 * stub their tab while waiting on a backend (e.g. memory write, traces
 * API) can re-use the chrome with a hand-written body — see the
 * `<TabPlaceholder>` re-render in `EXP-tab-unit-memory` once that lands.
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
