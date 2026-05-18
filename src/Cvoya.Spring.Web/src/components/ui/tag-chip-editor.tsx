"use client";

// Shared tag-chip editor (ADR-0046 Phase 4). One component drives both
// the per-membership Roles row (`variant="row"` — chips wrap on multiple
// lines) and the per-membership Expertise / Notifications stack
// (`variant="stack"` — one chip per line because expertise strings can
// be long).
//
// Chips render via the existing `<Badge variant="secondary">` primitive
// with an inline × button after the label. Below the chip area, a
// textbox + "Add" button row appends new values; Enter in the textbox
// also adds. Whitespace-only input is rejected and duplicates are
// suppressed (case-insensitive by default per @savasp's UX spec).

import { X } from "lucide-react";
import {
  forwardRef,
  useCallback,
  useId,
  useMemo,
  useState,
  type HTMLAttributes,
  type KeyboardEvent,
} from "react";

import { Badge } from "@/components/ui/badge";
import { Button } from "@/components/ui/button";
import { Input } from "@/components/ui/input";
import { cn } from "@/lib/utils";

export type TagChipEditorVariant = "row" | "stack";

export interface TagChipEditorProps
  extends Omit<HTMLAttributes<HTMLDivElement>, "onChange"> {
  /** Current set of chip values; renders one chip per entry. */
  values: readonly string[];
  /** Fires with the updated array on add / remove. */
  onChange: (next: string[]) => void;
  /** Placeholder copy for the inline textbox. */
  placeholder?: string;
  /** Label on the Add button. Defaults to "Add". */
  addButtonLabel?: string;
  /**
   * `row` wraps chips on multiple lines (`flex flex-wrap gap-2`).
   * `stack` renders one chip per line (`flex flex-col gap-1.5`). The
   * row variant is the default — most call sites are tag rows. The
   * stack variant exists because individual expertise strings may be
   * long enough that wrapping them inline reads poorly.
   */
  variant?: TagChipEditorVariant;
  /** When false (default), dedup compares case-insensitively. */
  caseSensitive?: boolean;
  /** Mirrors the surrounding form's disabled state. */
  disabled?: boolean;
  /**
   * Base test id. The editor itself gets this id; each chip's remove
   * button appends `-chip-${index}`; the textbox appends `-input`; the
   * add button appends `-add`.
   */
  testId?: string;
  /** Accessible name for the editor. Rendered via aria-label. */
  "aria-label"?: string;
}

/**
 * Tag chip editor. Pattern shared by the Unit × Members Roles row, the
 * per-membership Expertise stack, and the per-membership Notifications
 * stack — all three use the same component with the variant prop
 * controlling whether chips wrap horizontally or stack vertically.
 *
 * Add path: textbox + Add button (Enter in the textbox also adds).
 * Whitespace is trimmed; empty input is rejected; duplicates surface a
 * subtle "Already added" hint and refuse to add. Case-insensitive dedup
 * is the default; `caseSensitive` opts into strict comparison when a
 * future caller needs it.
 *
 * Remove path: each chip carries an inline × button (aria-labelled with
 * the chip value) that fires `onChange(values.filter(v => v !== value))`.
 * Duplicate-value handling: the filter removes every occurrence, which
 * is fine because the add path refuses to insert duplicates in the first
 * place.
 */
export const TagChipEditor = forwardRef<HTMLDivElement, TagChipEditorProps>(
  function TagChipEditor(
    {
      values,
      onChange,
      placeholder,
      addButtonLabel = "Add",
      variant = "row",
      caseSensitive = false,
      disabled = false,
      testId,
      className,
      "aria-label": ariaLabel,
      ...rest
    },
    ref,
  ) {
    const [draft, setDraft] = useState("");
    const inputId = useId();

    // Pre-compute the lowercase form of each value once so the dedup
    // probe below is O(n) per keystroke rather than O(n * m).
    const lowerValues = useMemo(
      () => (caseSensitive ? null : values.map((v) => v.toLowerCase())),
      [values, caseSensitive],
    );

    const trimmedDraft = draft.trim();
    const draftIsDuplicate = useMemo(() => {
      if (trimmedDraft.length === 0) return false;
      if (caseSensitive) return values.includes(trimmedDraft);
      const probe = trimmedDraft.toLowerCase();
      return lowerValues!.includes(probe);
    }, [trimmedDraft, values, lowerValues, caseSensitive]);

    const canAdd = !disabled && trimmedDraft.length > 0 && !draftIsDuplicate;

    const commitAdd = useCallback(() => {
      if (!canAdd) return;
      onChange([...values, trimmedDraft]);
      setDraft("");
    }, [canAdd, onChange, trimmedDraft, values]);

    const handleKeyDown = useCallback(
      (e: KeyboardEvent<HTMLInputElement>) => {
        if (e.key === "Enter") {
          // Enter is "Add", never "submit the surrounding form". The
          // dialog shells (and the dirty-state forms that host the
          // editor) all rely on the operator clicking Save explicitly,
          // so we suppress form submission here.
          e.preventDefault();
          commitAdd();
        }
      },
      [commitAdd],
    );

    const removeAt = (target: string) => {
      onChange(values.filter((v) => v !== target));
    };

    const chipContainerClass =
      variant === "stack"
        ? "flex flex-col gap-1.5"
        : "flex flex-wrap gap-2";

    return (
      <div
        ref={ref}
        className={cn("space-y-2", className)}
        data-testid={testId}
        aria-label={ariaLabel}
        role={ariaLabel ? "group" : undefined}
        {...rest}
      >
        {values.length > 0 && (
          <ul
            className={chipContainerClass}
            data-testid={testId ? `${testId}-chips` : undefined}
          >
            {values.map((value, index) => (
              <li
                key={`${value}-${index}`}
                className={variant === "stack" ? "flex" : undefined}
              >
                <Badge
                  variant="secondary"
                  className="inline-flex items-center gap-1 pr-1"
                >
                  <span className="truncate">{value}</span>
                  <button
                    type="button"
                    onClick={() => removeAt(value)}
                    disabled={disabled}
                    aria-label={`Remove ${value}`}
                    data-testid={
                      testId ? `${testId}-chip-${index}` : undefined
                    }
                    className="inline-flex h-4 w-4 items-center justify-center rounded-full text-secondary-foreground/70 transition-colors hover:bg-secondary-foreground/10 hover:text-secondary-foreground focus-visible:outline-none focus-visible:ring-1 focus-visible:ring-ring disabled:cursor-not-allowed disabled:opacity-50"
                  >
                    <X className="h-3 w-3" aria-hidden="true" />
                  </button>
                </Badge>
              </li>
            ))}
          </ul>
        )}

        <div className="flex items-center gap-2">
          <Input
            id={inputId}
            type="text"
            value={draft}
            placeholder={placeholder}
            disabled={disabled}
            onChange={(e) => setDraft(e.target.value)}
            onKeyDown={handleKeyDown}
            data-testid={testId ? `${testId}-input` : undefined}
            className={cn(
              draftIsDuplicate && "border-destructive focus-visible:ring-destructive",
            )}
            aria-invalid={draftIsDuplicate || undefined}
            aria-describedby={
              draftIsDuplicate && testId ? `${testId}-duplicate-hint` : undefined
            }
          />
          <Button
            type="button"
            onClick={commitAdd}
            disabled={!canAdd}
            data-testid={testId ? `${testId}-add` : undefined}
          >
            {addButtonLabel}
          </Button>
        </div>
        {draftIsDuplicate && (
          <p
            id={testId ? `${testId}-duplicate-hint` : undefined}
            className="text-xs text-destructive"
            data-testid={testId ? `${testId}-duplicate-hint` : undefined}
            role="status"
          >
            Already added.
          </p>
        )}
      </div>
    );
  },
);
