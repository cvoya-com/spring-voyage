"use client";

import { cn } from "@/lib/utils";
import {
  useCallback,
  useEffect,
  useId,
  useRef,
  type KeyboardEvent,
  type MouseEvent,
  type ReactNode,
} from "react";

interface DialogProps {
  /** Whether the dialog is visible. */
  open: boolean;
  /** Called when the user requests to close the dialog (ESC, backdrop, close button). */
  onClose: () => void;
  /**
   * Accessible name. Rendered as the heading inside the dialog body and also
   * referenced by `aria-labelledby`.
   */
  title: string;
  /** Optional supporting description shown under the title. */
  description?: string;
  /** Dialog body content (form fields, confirmation copy, etc.). */
  children: ReactNode;
  /** Action row rendered at the bottom of the dialog (Cancel / Submit). */
  footer?: ReactNode;
  /** Optional extra className for the dialog panel. */
  className?: string;
}

/**
 * Minimal accessible modal dialog. We avoid pulling in Radix to keep the
 * bundle lean; the implementation below is enough for the portal's current
 * needs:
 *
 *  - `role="dialog"` + `aria-modal="true"` + `aria-labelledby`.
 *  - ESC closes the dialog.
 *  - Click on the backdrop closes the dialog; click inside does not bubble.
 *  - Focus moves to the first focusable element on open, TAB cycles within
 *    the panel (a minimal focus trap), and focus is returned to the element
 *    that opened the dialog when it closes.
 *
 * If the platform later needs richer behavior (portal, scroll lock nuances,
 * stacked dialogs), we swap the internals without changing the public API.
 */
export function Dialog({
  open,
  onClose,
  title,
  description,
  children,
  footer,
  className,
}: DialogProps) {
  const titleId = useId();
  const descriptionId = useId();
  const panelRef = useRef<HTMLDivElement>(null);
  const previousFocusRef = useRef<HTMLElement | null>(null);

  // Remember the element that had focus before we opened, and restore it
  // when we close. Running this as a layout-adjacent effect keeps the
  // restore in the same tick as the DOM unmount, so focus doesn't flicker
  // to <body> in between.
  useEffect(() => {
    if (!open) return;
    previousFocusRef.current = document.activeElement as HTMLElement | null;
    return () => {
      previousFocusRef.current?.focus?.();
    };
  }, [open]);

  // Move focus into the panel once it mounts. We pick the first focusable
  // element; if none exists (rare, e.g., read-only dialog), we focus the
  // panel itself (it has tabIndex=-1).
  useEffect(() => {
    if (!open) return;
    const panel = panelRef.current;
    if (!panel) return;
    const focusable = panel.querySelector<HTMLElement>(
      'a[href], button:not([disabled]), textarea:not([disabled]), input:not([disabled]), select:not([disabled]), [tabindex]:not([tabindex="-1"])',
    );
    (focusable ?? panel).focus();
  }, [open]);

  // Close on ESC. Bound to the window because focus might be inside a
  // deeply-nested field and React's onKeyDown on the panel still bubbles,
  // but handling it once at the window level avoids duplicate listeners
  // when nested components opt out of bubbling.
  useEffect(() => {
    if (!open) return;
    const onKey = (e: globalThis.KeyboardEvent) => {
      if (e.key === "Escape") {
        e.preventDefault();
        onClose();
      }
    };
    window.addEventListener("keydown", onKey);
    return () => window.removeEventListener("keydown", onKey);
  }, [open, onClose]);

  // Lock body scroll while the dialog is open. Reset on unmount so we
  // don't leak the `overflow: hidden` style if the component is torn down
  // without the `open` prop first going false.
  useEffect(() => {
    if (!open) return;
    const previous = document.body.style.overflow;
    document.body.style.overflow = "hidden";
    return () => {
      document.body.style.overflow = previous;
    };
  }, [open]);

  // Minimal focus trap: on Tab / Shift+Tab at the boundary, loop back.
  const handleTabTrap = useCallback(
    (e: KeyboardEvent<HTMLDivElement>) => {
      if (e.key !== "Tab") return;
      const panel = panelRef.current;
      if (!panel) return;
      const focusables = panel.querySelectorAll<HTMLElement>(
        'a[href], button:not([disabled]), textarea:not([disabled]), input:not([disabled]), select:not([disabled]), [tabindex]:not([tabindex="-1"])',
      );
      if (focusables.length === 0) return;
      const first = focusables[0];
      const last = focusables[focusables.length - 1];
      const active = document.activeElement as HTMLElement | null;
      if (e.shiftKey && active === first) {
        e.preventDefault();
        last.focus();
      } else if (!e.shiftKey && active === last) {
        e.preventDefault();
        first.focus();
      }
    },
    [],
  );

  if (!open) return null;

  // Backdrop click closes; inside-panel click must not bubble up and
  // trigger close. We accomplish this by stopping propagation on the
  // panel's mousedown event (not click, to avoid swallowing click events
  // that children care about).
  const onBackdropMouseDown = (e: MouseEvent<HTMLDivElement>) => {
    if (e.target === e.currentTarget) onClose();
  };

  return (
    <div
      className="fixed inset-0 z-50 flex items-center justify-center bg-black/50 p-4"
      onMouseDown={onBackdropMouseDown}
      data-testid="dialog-backdrop"
    >
      <div
        ref={panelRef}
        role="dialog"
        aria-modal="true"
        aria-labelledby={titleId}
        aria-describedby={description ? descriptionId : undefined}
        tabIndex={-1}
        onKeyDown={handleTabTrap}
        className={cn(
          "relative w-full max-w-lg rounded-lg border border-border bg-card p-6 shadow-xl focus:outline-none",
          "max-h-[calc(100vh-2rem)] overflow-y-auto",
          className,
        )}
      >
        <div className="mb-4 space-y-1">
          <h2 id={titleId} className="text-lg font-semibold text-card-foreground">
            {title}
          </h2>
          {description && (
            <p id={descriptionId} className="text-sm text-muted-foreground">
              {description}
            </p>
          )}
        </div>
        <div className="space-y-4">{children}</div>
        {footer && (
          <div className="mt-6 flex justify-end gap-2 border-t border-border pt-4">
            {footer}
          </div>
        )}
      </div>
    </div>
  );
}
