"use client";

// Settings drawer (#451 — PR-S1 Sub-PR D). Renders a right-aligned
// modal drawer whose body is a stack of panels. Each panel comes from
// the extension registry (`@/lib/extensions`); OSS ships Budget /
// About / Auth defaults, and the hosted build plugs in additional
// panels (tenant secrets, members / RBAC, SSO) through the same
// surface. See `src/lib/extensions/README.md` for the extension
// contract and `DESIGN.md` § 7.x for the drawer conventions.

import {
  useCallback,
  useEffect,
  useId,
  useRef,
  type KeyboardEvent,
  type MouseEvent,
} from "react";

import { useExtensions } from "@/lib/extensions";
import type { DrawerPanel } from "@/lib/extensions";
import { cn } from "@/lib/utils";
import { X } from "lucide-react";

interface SettingsDrawerProps {
  /** Whether the drawer is visible. */
  open: boolean;
  /** Called when the user requests to close (ESC, backdrop, close button). */
  onClose: () => void;
}

/**
 * Right-aligned modal drawer. Conventions:
 *
 * - `role="dialog"` + `aria-modal="true"` + `aria-labelledby` on the
 *   heading.
 * - ESC closes; backdrop mousedown closes; click inside the panel does
 *   not bubble.
 * - Focus moves to the first focusable element on open; TAB / Shift-Tab
 *   cycles within the panel; focus returns to the opener on close.
 * - Body scroll is locked while open and restored on unmount.
 * - Panel ordering is driven entirely by the extension registry's
 *   `orderHint` rule — the drawer itself makes no assumptions about
 *   which or how many panels render.
 */
export function SettingsDrawer({ open, onClose }: SettingsDrawerProps) {
  const titleId = useId();
  const panelRef = useRef<HTMLDivElement>(null);
  const previousFocusRef = useRef<HTMLElement | null>(null);

  const { drawerPanels, auth } = useExtensions();

  // Permission gate — matches the sidebar's route filter so hosted
  // panels with a `permission` that the auth adapter rejects disappear
  // silently rather than rendering empty chrome.
  const visiblePanels = drawerPanels.filter(
    (p) => !p.permission || auth.hasPermission(p.permission),
  );

  // Remember the opener so focus returns there on close.
  useEffect(() => {
    if (!open) return;
    previousFocusRef.current = document.activeElement as HTMLElement | null;
    return () => {
      previousFocusRef.current?.focus?.();
    };
  }, [open]);

  // Move focus into the drawer on mount. Pick the close button (the
  // drawer always renders one) so keyboard users can dismiss without
  // tabbing through every panel.
  useEffect(() => {
    if (!open) return;
    const p = panelRef.current;
    if (!p) return;
    const focusable = p.querySelector<HTMLElement>(
      'a[href], button:not([disabled]), textarea:not([disabled]), input:not([disabled]), select:not([disabled]), [tabindex]:not([tabindex="-1"])',
    );
    (focusable ?? p).focus();
  }, [open]);

  // ESC closes.
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

  // Lock body scroll while the drawer is open.
  useEffect(() => {
    if (!open) return;
    const previous = document.body.style.overflow;
    document.body.style.overflow = "hidden";
    return () => {
      document.body.style.overflow = previous;
    };
  }, [open]);

  // Minimal focus trap.
  const handleTabTrap = useCallback(
    (e: KeyboardEvent<HTMLDivElement>) => {
      if (e.key !== "Tab") return;
      const p = panelRef.current;
      if (!p) return;
      const focusables = p.querySelectorAll<HTMLElement>(
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

  const onBackdropMouseDown = (e: MouseEvent<HTMLDivElement>) => {
    if (e.target === e.currentTarget) onClose();
  };

  return (
    <div
      className="fixed inset-0 z-50 flex justify-end bg-black/50"
      onMouseDown={onBackdropMouseDown}
      data-testid="settings-drawer-backdrop"
    >
      <div
        ref={panelRef}
        role="dialog"
        aria-modal="true"
        aria-labelledby={titleId}
        tabIndex={-1}
        onKeyDown={handleTabTrap}
        className={cn(
          "relative flex h-full w-full max-w-md flex-col border-l border-border bg-card shadow-xl focus:outline-none",
          "animate-in slide-in-from-right-2",
        )}
        data-testid="settings-drawer"
      >
        <div className="flex items-center justify-between border-b border-border px-6 py-4">
          <h2
            id={titleId}
            className="text-lg font-semibold text-card-foreground"
          >
            Settings
          </h2>
          <button
            onClick={onClose}
            className="rounded-md p-1 text-muted-foreground hover:bg-accent hover:text-accent-foreground focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring"
            aria-label="Close settings"
          >
            <X className="h-4 w-4" />
          </button>
        </div>
        <div className="flex-1 space-y-4 overflow-y-auto px-6 py-6">
          {visiblePanels.length === 0 ? (
            <p className="text-sm text-muted-foreground">
              No settings panels registered.
            </p>
          ) : (
            visiblePanels.map((panel) => (
              <DrawerPanelCard key={panel.id} panel={panel} />
            ))
          )}
        </div>
      </div>
    </div>
  );
}

function DrawerPanelCard({ panel }: { panel: DrawerPanel }) {
  const Icon = panel.icon;
  return (
    <section
      data-testid={`settings-panel-${panel.id}`}
      className="rounded-lg border border-border bg-background p-4"
    >
      <header className="mb-3 space-y-1">
        <div className="flex items-center gap-2 text-sm font-semibold text-card-foreground">
          <Icon className="h-4 w-4" />
          {panel.label}
        </div>
        {panel.description && (
          <p className="text-xs text-muted-foreground">{panel.description}</p>
        )}
      </header>
      <div className="text-sm">{panel.component}</div>
    </section>
  );
}
