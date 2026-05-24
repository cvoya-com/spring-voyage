"use client";

import { Check, Copy } from "lucide-react";
import { useState, type MouseEvent } from "react";

import { cn } from "@/lib/utils";

interface CopyEntryButtonProps {
  /**
   * The activity entry to copy. JSON-stringified with two-space indent so
   * the result is paste-ready into bug reports, Slack, or a scratch file
   * without further formatting.
   */
  entry: unknown;
  /** Extra classes for caller-side positioning / spacing. */
  className?: string;
  /**
   * Test-id forwarded onto the button. Callers pass a row-scoped value
   * (e.g. `activity-event-<id>`) so per-row interactions stay unambiguous;
   * a default falls back to `activity-entry-copy` for the single-row case.
   */
  testId?: string;
}

/**
 * Icon-only "copy this entry" button rendered next to an activity row's
 * title (#2562). Mirrors `<CopyAddressButton>` in `unit-detail-pane.tsx`:
 * swap to a check glyph for ~1.5 s on success, swallow clipboard failures
 * (insecure origin / permission denied) silently since the surface has no
 * toast bus to dispatch to.
 *
 * The click handler calls `stopPropagation` so wrappers that already treat
 * the row as clickable (the tenant-wide `/activity` page expands on row
 * click) don't toggle the row when the user only meant to copy.
 */
export function CopyEntryButton({
  entry,
  className,
  testId = "activity-entry-copy",
}: CopyEntryButtonProps) {
  const [copied, setCopied] = useState(false);

  const handleCopy = async (e: MouseEvent<HTMLButtonElement>) => {
    e.stopPropagation();
    e.preventDefault();
    try {
      await navigator.clipboard.writeText(JSON.stringify(entry, null, 2));
      setCopied(true);
      window.setTimeout(() => setCopied(false), 1500);
    } catch {
      // Clipboard can fail on insecure origins or when the user denies
      // permission. Silent — same posture as `<CopyAddressButton>`.
    }
  };

  return (
    <button
      type="button"
      onClick={handleCopy}
      aria-label={copied ? "Entry copied" : "Copy entry"}
      data-testid={testId}
      className={cn(
        "inline-flex h-6 w-6 shrink-0 items-center justify-center rounded text-muted-foreground transition-colors hover:bg-accent hover:text-foreground focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring",
        className,
      )}
    >
      {copied ? (
        <Check className="h-3.5 w-3.5" aria-hidden="true" />
      ) : (
        <Copy className="h-3.5 w-3.5" aria-hidden="true" />
      )}
    </button>
  );
}
