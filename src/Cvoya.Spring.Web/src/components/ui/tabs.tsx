"use client";

// Minimal tabs primitive wired to the WAI-ARIA Tabs pattern
// (https://www.w3.org/WAI/ARIA/apg/patterns/tabs/). `TabsList` is the
// `role="tablist"` container, each `TabsTrigger` is a `role="tab"` that
// toggles its own `TabsContent` (`role="tabpanel"`) via matching
// `aria-controls` / `id` pairs. Left / Right arrow keys move focus
// between tabs; Home / End jump to the first / last tab; activation is
// automatic (tab acquires focus → panel swaps) per the common
// "follow focus" variant used by the portal.

import { cn } from "@/lib/utils";
import {
  createContext,
  useCallback,
  useContext,
  useId,
  useMemo,
  useRef,
  useState,
  type KeyboardEvent,
  type ReactNode,
} from "react";

interface TabsContextValue {
  value: string;
  onValueChange: (value: string) => void;
  /**
   * Stable base used to mint `aria-controls` / `id` attributes that
   * pair a `<TabsTrigger value="…">` with its matching `<TabsContent>`.
   * Each tab derives `tab-${base}-${value}` for its id and
   * `panel-${base}-${value}` for its controlled panel — consistent
   * across renders so the a11y tree stays stable.
   */
  baseId: string;
  /** Registers a trigger's DOM node so arrow-key nav can focus siblings. */
  registerTrigger: (value: string, el: HTMLButtonElement | null) => void;
  /** Focus the nearest tab given a direction ("prev" | "next" | "first" | "last"). */
  focusTrigger: (from: string, direction: "prev" | "next" | "first" | "last") => void;
}

const TabsContext = createContext<TabsContextValue | null>(null);

function useTabsContext(): TabsContextValue {
  const ctx = useContext(TabsContext);
  if (!ctx) {
    throw new Error("Tabs children must be rendered inside <Tabs>");
  }
  return ctx;
}

export function Tabs({
  defaultValue,
  children,
  className,
}: {
  defaultValue: string;
  children: ReactNode;
  className?: string;
}) {
  const baseId = useId();
  const [value, setValue] = useState(defaultValue);
  // Track trigger refs in insertion order so arrow-key navigation can
  // pick the previous / next sibling regardless of how the caller
  // arranges them. A ref map (not state) keeps this side-effect free.
  const triggersRef = useRef<Array<{ value: string; el: HTMLButtonElement }>>(
    [],
  );

  const registerTrigger = useCallback(
    (v: string, el: HTMLButtonElement | null) => {
      const list = triggersRef.current;
      const existing = list.findIndex((t) => t.value === v);
      if (el === null) {
        if (existing >= 0) list.splice(existing, 1);
        return;
      }
      if (existing >= 0) {
        list[existing] = { value: v, el };
      } else {
        list.push({ value: v, el });
      }
    },
    [],
  );

  const focusTrigger = useCallback(
    (from: string, direction: "prev" | "next" | "first" | "last") => {
      const list = triggersRef.current;
      if (list.length === 0) return;
      let nextIndex = list.findIndex((t) => t.value === from);
      if (direction === "first") {
        nextIndex = 0;
      } else if (direction === "last") {
        nextIndex = list.length - 1;
      } else if (direction === "prev") {
        nextIndex = nextIndex <= 0 ? list.length - 1 : nextIndex - 1;
      } else {
        nextIndex = nextIndex < 0 || nextIndex >= list.length - 1 ? 0 : nextIndex + 1;
      }
      const next = list[nextIndex];
      if (!next) return;
      next.el.focus();
      // "Follow focus" activation — swap the active panel when focus
      // moves. Matches the APG automatic-activation variant.
      setValue(next.value);
    },
    [],
  );

  const ctx = useMemo<TabsContextValue>(
    () => ({
      value,
      onValueChange: setValue,
      baseId,
      registerTrigger,
      focusTrigger,
    }),
    [value, baseId, registerTrigger, focusTrigger],
  );

  return (
    <TabsContext.Provider value={ctx}>
      <div className={className}>{children}</div>
    </TabsContext.Provider>
  );
}

export function TabsList({
  children,
  className,
  "aria-label": ariaLabel,
}: {
  children: ReactNode;
  className?: string;
  "aria-label"?: string;
}) {
  // At narrow viewports the tab list can carry more triggers than fit in
  // the viewport (e.g. the unit-detail tab bar has 11 tabs). Wrap the
  // inner flex row in an overflow-x-auto container so the bar scrolls
  // horizontally instead of forcing the whole page to overflow. `w-full`
  // on the outer wrapper keeps the scrollable region bounded to the
  // card / page column; the inner `inline-flex` preserves the pill
  // chrome DESIGN.md § 7.7 describes.
  return (
    <div className="w-full overflow-x-auto">
      <div
        role="tablist"
        aria-label={ariaLabel}
        className={cn(
          "inline-flex h-9 items-center justify-start rounded-lg bg-muted p-1 text-muted-foreground",
          className,
        )}
      >
        {children}
      </div>
    </div>
  );
}

export function TabsTrigger({
  value,
  children,
  className,
}: {
  value: string;
  children: ReactNode;
  className?: string;
}) {
  const ctx = useTabsContext();
  const selected = ctx.value === value;
  const tabId = `tab-${ctx.baseId}-${value}`;
  const panelId = `panel-${ctx.baseId}-${value}`;

  const handleKeyDown = (e: KeyboardEvent<HTMLButtonElement>) => {
    switch (e.key) {
      case "ArrowLeft":
        e.preventDefault();
        ctx.focusTrigger(value, "prev");
        break;
      case "ArrowRight":
        e.preventDefault();
        ctx.focusTrigger(value, "next");
        break;
      case "Home":
        e.preventDefault();
        ctx.focusTrigger(value, "first");
        break;
      case "End":
        e.preventDefault();
        ctx.focusTrigger(value, "last");
        break;
      default:
        break;
    }
  };

  return (
    <button
      role="tab"
      type="button"
      id={tabId}
      aria-selected={selected}
      aria-controls={panelId}
      // Roving tabindex — only the selected tab receives natural Tab
      // focus; arrow keys move focus between the rest.
      tabIndex={selected ? 0 : -1}
      ref={(el) => ctx.registerTrigger(value, el)}
      onClick={() => ctx.onValueChange(value)}
      onKeyDown={handleKeyDown}
      className={cn(
        "inline-flex items-center justify-center whitespace-nowrap rounded-md px-3 py-1 text-sm font-medium transition-all focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring",
        selected
          ? "bg-background text-foreground shadow-sm"
          : "hover:text-foreground",
        className,
      )}
    >
      {children}
    </button>
  );
}

export function TabsContent({
  value,
  children,
  className,
}: {
  value: string;
  children: ReactNode;
  className?: string;
}) {
  const ctx = useTabsContext();
  if (ctx.value !== value) return null;
  const tabId = `tab-${ctx.baseId}-${value}`;
  const panelId = `panel-${ctx.baseId}-${value}`;
  return (
    <div
      role="tabpanel"
      id={panelId}
      aria-labelledby={tabId}
      tabIndex={0}
      className={cn("mt-2 focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring rounded-md", className)}
    >
      {children}
    </div>
  );
}
