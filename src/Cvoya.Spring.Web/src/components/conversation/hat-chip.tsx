// <HatChip> — read-side per-row indicator for the receiving Hat
// (ADR-0062 § 5, #2807 + #2826 + #2829).
//
// PR #2825 (#2807) introduced the per-row chip on the inbox list — each
// row's Hat label reads "As <name>" so the operator sees which of their
// bound Humans actually received the inbound. PR #2828 (#2826 Part 1)
// extended the same chip to the engagement list and the unit / agent
// messaging-tab via `ThreadSummaryResponse.recipientHumanDisplayName`.
// #2829 promoted the wire field from the raw display name to the
// server-computed `disambiguatedLabel` (e.g. "Bob — designer" /
// "Bob (Magazine)" / "Bob #12ab") so same-name Hats stay distinct on
// every surface — inbox chip, engagement chip, messaging-tab banner,
// from-selector, your-hats panel, and the inbox toolbar filter chip
// (#2826 Part 2). The portal renders the server's string verbatim;
// there is no client-side disambiguation derivation.
//
// Visual contract: low-key outline badge, 10px text, tight horizontal
// padding — sized to slot under a thread-row title without disturbing
// the row's existing affordances. Returns `null` when no label is
// supplied so callers can pass the field straight from the wire shape
// without an extra `&&` gate. See `src/Cvoya.Spring.Web/DESIGN.md` § 5.6
// (Hat chip) for the canonical pattern.

import { Badge } from "@/components/ui/badge";
import { cn } from "@/lib/utils";

export interface HatChipProps {
  /**
   * Disambiguated label for the receiving Hat (Human) for the row this
   * chip belongs to. Forwarded straight from the server-computed
   * `disambiguatedLabel` wire field — `null` / undefined / blank
   * suppresses the chip entirely so the caller does not have to short-
   * circuit on its own. The server applies the
   * role / unit / Guid-suffix disambiguation rule (ADR-0062 § 5 / #2829)
   * before rendering; the portal never derives the label client-side.
   */
  label?: string | null;
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
 * Renders the "As &lt;Hat label&gt;" indicator that appears on each
 * thread row. Centralises the visual treatment so the inbox,
 * engagement list, and unit/agent messaging-tab all match — and so the
 * inbox toolbar filter chip (#2826 Part 2) can reuse the same
 * affordance.
 */
export function HatChip({ label, testId, className }: HatChipProps) {
  const trimmed = label?.trim();
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
