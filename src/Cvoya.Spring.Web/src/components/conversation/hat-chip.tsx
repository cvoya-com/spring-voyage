// <HatChip> — read-side per-row indicator for the receiving Hat
// (ADR-0062 § 5, #2807 + #2826).
//
// PR #2825 (#2807) introduced the per-row chip on the inbox list — each
// row's `item.human.displayName` reads "As <name>" so the operator sees
// which of their bound Humans actually received the inbound. #2826
// extends the same chip to the engagement list and the unit / agent
// messaging-tab, where the underlying data lands as
// `ThreadSummaryResponse.recipientHumanDisplayName`.
//
// Visual contract: low-key outline badge, 10px text, tight horizontal
// padding — sized to slot under a thread-row title without disturbing
// the row's existing affordances. Returns `null` when no display name is
// supplied so callers can pass the field straight from the wire shape
// without an extra `&&` gate. See `src/Cvoya.Spring.Web/DESIGN.md` § 5.6
// (Hat chip) for the canonical pattern.

import { Badge } from "@/components/ui/badge";
import { cn } from "@/lib/utils";

export interface HatChipProps {
  /**
   * Display name of the receiving Hat (Human) for the row this chip
   * belongs to. Forwarded straight from the wire shape — `null` /
   * undefined / blank suppresses the chip entirely so the caller does
   * not have to short-circuit on its own.
   */
  displayName?: string | null;
  /**
   * Stable `data-testid` for the chip. Surface-scoped so tests on the
   * inbox / engagement list / messaging tab can target each rendering
   * independently (e.g. `inbox-hat-chip-<threadId>`).
   */
  testId?: string;
  /** Optional className override for surfaces that need extra spacing. */
  className?: string;
}

/**
 * Renders the "As &lt;Hat name&gt;" indicator that appears on each
 * thread row. Centralises the visual treatment so the inbox,
 * engagement list, and unit/agent messaging-tab all match — and so the
 * Part 2 work tracked on #2826 (lane / filter treatments) can swap the
 * one component instead of three.
 */
export function HatChip({ displayName, testId, className }: HatChipProps) {
  const trimmed = displayName?.trim();
  if (!trimmed) {
    return null;
  }
  return (
    <Badge
      variant="outline"
      className={cn("h-4 px-1.5 text-[10px] font-normal", className)}
      data-testid={testId}
      title={`Received as ${trimmed}`}
    >
      As {trimmed}
    </Badge>
  );
}
