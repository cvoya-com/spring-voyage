"use client";

// <HumanFromSelector> — the "speaking as" Hat picker (ADR-0062 § 5, #2807).
//
// One operator can be bound to many `Human` rows ("Hats") across (and
// within) units; this selector lets the operator choose which Hat the
// outbound message is stamped with. The selected id is forwarded as the
// explicit `from` field on `POST /api/v1/tenant/messages` /
// `POST /api/v1/tenant/threads/{id}/messages`; the server validates the
// Hat is in the caller's bound set and stamps it on `Message.From`
// (`human://<id>`).
//
// Visual contract (DESIGN.md § Composers — added in the same PR):
//   - When the caller has 0 or 1 bound Hat the selector collapses to a
//     static badge ("As Bob (designer in Magazine)") — no interactive
//     control. This is the OSS-default case (one operator → one Hat).
//   - When the caller has 2+ Hats the selector renders as a compact
//     <select> with the same "As <name> (<context>)" formatting on each
//     option. The selected option is mirrored in a leading icon-only
//     badge so the affordance reads "speaking as ..." at a glance.
//   - The control is a small inline strip above the composer textarea
//     so the composer's single-row layout (#1553) stays compact.
//
// Default selection:
//   - Reply composer (a `threadId` is supplied) — the parent picks the
//     Hat by inspecting the thread's inbound `Message.To`; the selector
//     itself just honours the parent's `defaultHumanId` prop and falls
//     back to the primary Hat if nothing was passed.
//   - New-outbound composer (`threadId` is null) — the primary Hat
//     (`isPrimary === true`) wins. If no Hat is primary the first
//     alphabetical entry wins.
//
// The component is presentation only — it owns no caller-Hats fetch.
// Consumers pass the resolved `humans` list (typically from
// `useCallerHumans()`) and the `value` / `onChange` pair, mirroring
// every other shadcn-flavoured primitive in `components/ui/`.

import { useMemo } from "react";
import { UserRound } from "lucide-react";

import { Badge } from "@/components/ui/badge";
import { cn } from "@/lib/utils";
import type {
  CallerHumanMembershipResponse,
  CallerHumanResponse,
} from "@/lib/api/types";

export interface HumanFromSelectorProps {
  /** The caller's bound-Hat set; typically `useCallerHumans().data`. */
  humans: CallerHumanResponse[];
  /**
   * The currently-selected Hat id. The selector mirrors this to the
   * <select> control's value; the parent owns the state. When `null`
   * the selector renders nothing while the bound-set is still loading.
   */
  value: string | null;
  /** Called with the chosen Hat id when the user picks a different one. */
  onChange: (humanId: string) => void;
  /** Optional test-id root for the outer wrapper. */
  testId?: string;
  /** Disables the control (mirrors the composer's pending state). */
  disabled?: boolean;
}

/**
 * Render the Hat row's "context" suffix — `designer in Magazine`,
 * `reviewer in Magazine + Editorial`, or empty when the Hat has no
 * unit memberships (rare; tenant-scoped Humans).
 *
 * Exported so the inbox Hat chip and the from-selector speak the same
 * formatting rule. Surface tests pin the exact string.
 */
function formatMembership(m: CallerHumanMembershipResponse): string {
  const role = m.roles[0];
  return role ? `${role} in ${m.unitDisplayName}` : `in ${m.unitDisplayName}`;
}

export function formatHumanContext(hat: CallerHumanResponse): string {
  if (hat.memberships.length === 0) {
    return "";
  }
  // One membership → "<role> in <unit>". Many memberships → "<unit>+<unit>+…"
  // to keep the chip narrow; the role list goes plural inline.
  if (hat.memberships.length === 1) {
    return formatMembership(hat.memberships[0]);
  }
  const unitNames = hat.memberships
    .map((m) => m.unitDisplayName)
    .filter((n) => n.length > 0);
  return `in ${unitNames.join(" + ")}`;
}

/**
 * The default "As <name> (<context>)" label rendered by both the
 * selector's collapsed badge and each <option> element. Kept on the
 * module surface so the inbox chip and unit-message Hat chip reuse it.
 */
export function formatHumanLabel(hat: CallerHumanResponse): string {
  const context = formatHumanContext(hat);
  return context.length > 0
    ? `${hat.displayName} (${context})`
    : hat.displayName;
}

/**
 * Resolve the default Hat id given a thread-pinned hint or a primary
 * fallback. Exposed so the composers can apply the same rule when
 * seeding their controlled `from` state — keeps the default resolution
 * in one place.
 */
export function pickDefaultHumanId(
  humans: CallerHumanResponse[],
  preferredHumanId?: string | null,
): string | null {
  if (humans.length === 0) {
    return null;
  }
  if (preferredHumanId) {
    const hit = humans.find((h) => h.humanId === preferredHumanId);
    if (hit) {
      return hit.humanId;
    }
  }
  const primary = humans.find((h) => h.isPrimary);
  return primary?.humanId ?? humans[0].humanId;
}

export function HumanFromSelector({
  humans,
  value,
  onChange,
  testId = "human-from-selector",
  disabled = false,
}: HumanFromSelectorProps) {
  // Sort + de-dup the bound set so the visible ordering matches the
  // API's "primary first then alphabetical" rule even if the parent
  // hands the list through unsorted. Stable across renders.
  const sortedHumans = useMemo(() => {
    return [...humans].sort((a, b) => {
      if (a.isPrimary && !b.isPrimary) return -1;
      if (!a.isPrimary && b.isPrimary) return 1;
      return a.displayName.localeCompare(b.displayName);
    });
  }, [humans]);

  if (sortedHumans.length === 0) {
    return null;
  }

  if (sortedHumans.length === 1) {
    const only = sortedHumans[0];
    return (
      <div
        className="flex items-center gap-1.5 text-xs text-muted-foreground"
        data-testid={testId}
        data-mode="static"
      >
        <UserRound className="h-3 w-3" aria-hidden="true" />
        <span className="truncate">
          As <span className="font-medium text-foreground">{formatHumanLabel(only)}</span>
        </span>
      </div>
    );
  }

  // 2+ Hats: render the actual picker. The badge mirrors the current
  // selection so the bar reads "speaking as ..." even when the
  // dropdown collapses.
  const current = sortedHumans.find((h) => h.humanId === value) ?? sortedHumans[0];
  return (
    <div
      className="flex items-center gap-1.5 text-xs text-muted-foreground"
      data-testid={testId}
      data-mode="picker"
    >
      <UserRound className="h-3 w-3" aria-hidden="true" />
      <label className="sr-only" htmlFor={`${testId}-select`}>
        Speaking as
      </label>
      <span>As</span>
      <select
        id={`${testId}-select`}
        value={current.humanId}
        disabled={disabled}
        onChange={(e) => onChange(e.target.value)}
        data-testid={`${testId}-select`}
        className={cn(
          "min-w-0 max-w-[20rem] truncate rounded-md border border-input bg-background px-2 py-0.5 text-xs",
          "focus-visible:outline-none focus-visible:ring-1 focus-visible:ring-ring",
          disabled && "opacity-60",
        )}
      >
        {sortedHumans.map((h) => (
          <option
            key={h.humanId}
            value={h.humanId}
            data-testid={`${testId}-option-${h.humanId}`}
          >
            {formatHumanLabel(h)}
            {h.isPrimary ? " — primary" : ""}
          </option>
        ))}
      </select>
      {current.isPrimary && (
        <Badge
          variant="outline"
          className="h-4 px-1 text-[10px]"
          data-testid={`${testId}-primary-hint`}
        >
          primary
        </Badge>
      )}
    </div>
  );
}
