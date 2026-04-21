"use client";

import {
  Activity,
  Bot,
  Brain,
  Copy,
  LayoutDashboard,
  Layers,
  MessagesSquare,
  PiggyBank,
  Settings,
  ShieldCheck,
  Sparkles,
  Zap,
  type LucideIcon,
} from "lucide-react";
import type { MouseEvent } from "react";

import { cn } from "@/lib/utils";

import type { TabName } from "@/components/units/aggregate";

/**
 * The chip-row renders the same tab names the Explorer surface exposes.
 * Aliasing to `TabName` means adding a tab to any per-kind catalog
 * (`UNIT_TABS`, `AGENT_TABS`, `TENANT_TABS`) forces a matching entry in
 * {@link TAB_ICON} via the `Record<CardTabName, LucideIcon>` signature —
 * an unknown tab name fails type-checking instead of silently rendering
 * an iconless chip.
 */
export type CardTabName = TabName;

const TAB_ICON: Record<CardTabName, LucideIcon> = {
  Overview: LayoutDashboard,
  Agents: Bot,
  Orchestration: Layers,
  Activity: Activity,
  Messages: MessagesSquare,
  Memory: Brain,
  Policies: ShieldCheck,
  Skills: Sparkles,
  Traces: Zap,
  Clones: Copy,
  Config: Settings,
  Budgets: PiggyBank,
};

interface TabChipProps {
  tab: CardTabName;
  /**
   * The id of the entity the chip belongs to (unit id, agent id, …). Forwarded
   * verbatim to {@link onOpenTab} so the parent can route into the right
   * subject without an extra lookup.
   */
  id: string;
  onOpenTab: (id: string, tab: CardTabName) => void;
  /**
   * Optional override for the chip's accessible label. Defaults to
   * `Open {tab} tab`. Used by overview/dashboard cards that want a more
   * descriptive name (e.g. `Open Activity tab for Engineering`).
   */
  label?: string;
  className?: string;
}

/**
 * Single icon-only tab-deeplink button.
 *
 * Stops click propagation so a chip-row sitting inside a click-to-open card
 * (the dashboard's `<UnitCard>` / `<AgentCard>`) does NOT also trigger the
 * card's primary action — the chip explicitly routes to the named tab
 * instead of the default Overview.
 *
 * Accessibility: every chip is a real `<button>` with an `aria-label` so
 * screen readers hear "Open Activity tab" rather than reading the icon
 * glyph. The `title` attribute provides a native browser tooltip on hover
 * for sighted users.
 */
export function TabChip({
  tab,
  id,
  onOpenTab,
  label,
  className,
}: TabChipProps) {
  const Icon = TAB_ICON[tab];
  const accessibleLabel = label ?? `Open ${tab} tab`;
  const handle = (e: MouseEvent<HTMLButtonElement>) => {
    e.stopPropagation();
    onOpenTab(id, tab);
  };
  return (
    <button
      type="button"
      onClick={handle}
      aria-label={accessibleLabel}
      title={accessibleLabel}
      data-testid={`card-tab-chip-${tab.toLowerCase()}`}
      data-tab={tab}
      className={cn(
        // 28×28 ghost icon button: ghost surface, accent on hover, focus
        // ring picks up the brand colour via the Tailwind `--color-ring`
        // token.
        "inline-flex h-7 w-7 items-center justify-center rounded-md text-muted-foreground transition-colors hover:bg-accent hover:text-foreground focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring",
        className,
      )}
    >
      <Icon className="h-3.5 w-3.5" aria-hidden="true" />
    </button>
  );
}

interface CardTabRowProps {
  tabs: readonly CardTabName[];
  /** Forwarded to every chip; see {@link TabChipProps.id}. */
  id: string;
  onOpenTab: (id: string, tab: CardTabName) => void;
  className?: string;
}

/**
 * Footer chip-row for the dashboard `<UnitCard>` / `<AgentCard>` (and
 * the Explorer's `<ChildCard>`). Exposes a row of one-tap deeplinks
 * straight into a named Explorer tab so operators can jump from the card
 * grid into the right subject without first landing on Overview.
 *
 * Renders nothing when the `tabs` list is empty — callers can pass an
 * empty/optional array without conditionally guarding the JSX.
 *
 * The `onOpenTab` contract is shared with `<TabChip>`: each chip receives
 * the entity id at construction time and dispatches `(id, tab)` on click.
 */
export function CardTabRow({
  tabs,
  id,
  onOpenTab,
  className,
}: CardTabRowProps) {
  if (tabs.length === 0) return null;
  return (
    <div
      data-testid="card-tab-row"
      className={cn(
        "flex items-center gap-1 border-t border-border px-3 py-2",
        className,
      )}
    >
      {tabs.map((tab) => (
        <TabChip key={tab} tab={tab} id={id} onOpenTab={onOpenTab} />
      ))}
    </div>
  );
}
