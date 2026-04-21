"use client";

import {
  cloneElement,
  useCallback,
  useEffect,
  useId,
  useState,
  type FocusEvent,
  type KeyboardEvent,
  type MouseEvent,
  type ReactElement,
} from "react";

import { cn } from "@/lib/utils";

// Short delay before the tooltip shows — matches the 200 ms window the
// WAI-ARIA practice guide suggests for hover tooltips so mouse users
// don't get spammed while scanning. Keyboard focus shows immediately:
// once a control is focused the user has already committed attention,
// and waiting just hides helpful context.
const HOVER_DELAY_MS = 200;

interface TooltipProps {
  /**
   * Single focusable element that anchors the tooltip. Cloned with
   * `aria-describedby`, hover/focus handlers, and keyboard dismiss —
   * the child's own handlers are preserved.
   */
  children: ReactElement<{
    "aria-describedby"?: string;
    onMouseEnter?: (event: MouseEvent<HTMLElement>) => void;
    onMouseLeave?: (event: MouseEvent<HTMLElement>) => void;
    onFocus?: (event: FocusEvent<HTMLElement>) => void;
    onBlur?: (event: FocusEvent<HTMLElement>) => void;
    onKeyDown?: (event: KeyboardEvent<HTMLElement>) => void;
  }>;
  /** Text shown inside the tooltip bubble (also the accessible description). */
  label: string;
  /**
   * Side the tooltip bubble sits relative to the anchor. Defaults to
   * `right`, which is what the collapsed sidebar rail wants.
   */
  side?: "top" | "right" | "bottom" | "left";
  /**
   * When false, the tooltip is fully inert — no hover handlers, no
   * bubble in the DOM, no `aria-describedby`. Use this to turn the
   * tooltip off when the underlying label is already visible (e.g. the
   * expanded sidebar).
   */
  enabled?: boolean;
  /** Optional override for the short hover delay. */
  delayMs?: number;
}

const SIDE_CLASSES: Record<NonNullable<TooltipProps["side"]>, string> = {
  top: "bottom-full left-1/2 mb-2 -translate-x-1/2",
  right: "left-full top-1/2 ml-2 -translate-y-1/2",
  bottom: "top-full left-1/2 mt-2 -translate-x-1/2",
  left: "right-full top-1/2 mr-2 -translate-y-1/2",
};

// Enter animation — keep the transform small so the bubble glides in
// without drawing attention away from the anchor. Matches the existing
// `transition-*` token vocabulary used elsewhere in the sidebar.
const ENTER_TRANSFORM: Record<NonNullable<TooltipProps["side"]>, string> = {
  top: "translate-y-1",
  right: "-translate-x-1",
  bottom: "-translate-y-1",
  left: "translate-x-1",
};

/**
 * Accessible hover/focus tooltip. Matches the WAI-ARIA Authoring
 * Practices "tooltip" pattern:
 * https://www.w3.org/WAI/ARIA/apg/patterns/tooltip/
 *
 *  - `role="tooltip"` with a stable id wired to the anchor via
 *    `aria-describedby` (only while visible — screen readers shouldn't
 *    chase a hidden description).
 *  - Shows on hover *and* on focus so keyboard users get the same hint
 *    without pointing.
 *  - Dismisses on `mouseleave`, `blur`, and `Escape` — without
 *    bubbling the key event up so a parent collapse-toggle or dialog
 *    isn't accidentally dismissed too.
 *  - Fades + translates in using existing Tailwind transition tokens;
 *    the global `prefers-reduced-motion` rule drops the duration.
 */
type TooltipState =
  | { phase: "closed" }
  | { phase: "pending"; delayMs: number; generation: number }
  | { phase: "open" };

export function Tooltip({
  children,
  label,
  side = "right",
  enabled = true,
  delayMs = HOVER_DELAY_MS,
}: TooltipProps) {
  const id = useId();
  // Encoding the show-timer as state (rather than a ref) keeps the
  // react-hooks/refs rule happy: the cloned event handlers mutate state,
  // and an effect owns the `setTimeout` so the ref access lives
  // outside render.
  const [state, setState] = useState<TooltipState>({ phase: "closed" });
  const visible = state.phase === "open";

  useEffect(() => {
    if (state.phase !== "pending") return;
    const handle = setTimeout(() => {
      setState((prev) =>
        prev.phase === "pending" && prev.generation === state.generation
          ? { phase: "open" }
          : prev,
      );
    }, state.delayMs);
    return () => clearTimeout(handle);
  }, [state]);

  const show = useCallback(
    (immediate: boolean) => {
      if (!enabled) return;
      if (immediate || delayMs <= 0) {
        setState({ phase: "open" });
        return;
      }
      setState((prev) => ({
        phase: "pending",
        delayMs,
        generation:
          prev.phase === "pending" ? prev.generation + 1 : 0,
      }));
    },
    [delayMs, enabled],
  );

  const hide = useCallback(() => {
    setState({ phase: "closed" });
  }, []);

  if (!enabled) return children;

  const child = children;
  const childProps = child.props;

  const wrappedChild = cloneElement(child, {
    "aria-describedby": visible
      ? [childProps["aria-describedby"], id].filter(Boolean).join(" ") ||
        undefined
      : childProps["aria-describedby"],
    onMouseEnter: (event: MouseEvent<HTMLElement>) => {
      childProps.onMouseEnter?.(event);
      show(false);
    },
    onMouseLeave: (event: MouseEvent<HTMLElement>) => {
      childProps.onMouseLeave?.(event);
      hide();
    },
    onFocus: (event: FocusEvent<HTMLElement>) => {
      childProps.onFocus?.(event);
      show(true);
    },
    onBlur: (event: FocusEvent<HTMLElement>) => {
      childProps.onBlur?.(event);
      hide();
    },
    onKeyDown: (event: KeyboardEvent<HTMLElement>) => {
      childProps.onKeyDown?.(event);
      if (event.key === "Escape" && visible) {
        // Swallow so we don't bubble into parent collapse/dismiss
        // handlers (the dialog mounts a window-level Escape listener,
        // for example).
        event.stopPropagation();
        hide();
      }
    },
  });

  return (
    <span className="relative inline-flex" data-slot="tooltip-anchor">
      {wrappedChild}
      <span
        id={id}
        role="tooltip"
        data-testid="tooltip"
        data-side={side}
        data-state={visible ? "open" : "closed"}
        // Keep the tooltip node mounted so the transition can play on
        // the way out and `aria-describedby` can point at a stable id.
        // `aria-hidden` + `pointer-events-none` keep AT from announcing
        // the hidden content twice.
        aria-hidden={!visible}
        className={cn(
          "pointer-events-none absolute z-50 whitespace-nowrap rounded-md border border-border bg-popover px-2 py-1 text-xs font-medium text-popover-foreground shadow-md",
          "transition-[opacity,transform] duration-150 ease-out",
          SIDE_CLASSES[side],
          visible
            ? "opacity-100 translate-x-0 translate-y-0"
            : cn("opacity-0", ENTER_TRANSFORM[side]),
        )}
      >
        {label}
      </span>
    </span>
  );
}
