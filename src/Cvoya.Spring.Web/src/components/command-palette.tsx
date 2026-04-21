"use client";

// Global command palette (#439). Opens on `Cmd-K` (macOS), `Ctrl-K`
// (other platforms), and on `/` when the focused element is not an
// editable field. Indexes every route from the extension registry
// plus every palette action — OSS registers the core set today;
// hosted adds its own via `registerExtension({ routes, actions })`.
//
// We deliberately skip `cmdk`'s `Command.Dialog` (it ships with a
// Radix portal that is awkward to test in JSDOM and collides with
// our own toast / dialog stacking). Instead, we wrap the bare
// `Command` primitive in a lightweight modal shell that matches the
// rest of the portal's dialog styling.

import { cn } from "@/lib/utils";
import { useExplorerSelection } from "@/components/units/explorer-selection-context";
import { usePaletteActions, useRoutes } from "@/lib/extensions";
import type { PaletteAction, RouteEntry } from "@/lib/extensions";
import { Command as CommandRoot } from "cmdk";
import { ArrowRight, Search } from "lucide-react";
import { usePathname, useRouter } from "next/navigation";
import {
  createContext,
  useCallback,
  useContext,
  useEffect,
  useMemo,
  useRef,
  useState,
  type ReactNode,
} from "react";

// ---------------------------------------------------------------------------
// Public context API
// ---------------------------------------------------------------------------

interface CommandPaletteContextValue {
  /** Whether the palette is open. */
  open: boolean;
  /** Open the palette. */
  openPalette: () => void;
  /** Close the palette. */
  closePalette: () => void;
  /** Toggle the palette open state. */
  togglePalette: () => void;
}

const CommandPaletteContext = createContext<CommandPaletteContextValue | null>(
  null,
);

/**
 * Hook for consumers (top-bar buttons, onboarding tours, tests) that
 * need to open the palette programmatically.
 */
export function useCommandPalette(): CommandPaletteContextValue {
  const ctx = useContext(CommandPaletteContext);
  if (!ctx) {
    throw new Error("useCommandPalette must be used within CommandPaletteProvider");
  }
  return ctx;
}

// ---------------------------------------------------------------------------
// Provider + mounted palette
// ---------------------------------------------------------------------------

/**
 * Mounts the palette state and keyboard shortcut listener. The
 * palette itself renders (only when `open`) so the trigger dialog
 * isn't in the DOM until the user asks for it — keeps background
 * filter work at zero cost.
 */
export function CommandPaletteProvider({ children }: { children: ReactNode }) {
  const [open, setOpen] = useState(false);

  const value = useMemo<CommandPaletteContextValue>(
    () => ({
      open,
      openPalette: () => setOpen(true),
      closePalette: () => setOpen(false),
      togglePalette: () => setOpen((o) => !o),
    }),
    [open],
  );

  useGlobalPaletteShortcuts({
    open,
    setOpen,
  });

  return (
    <CommandPaletteContext.Provider value={value}>
      {children}
      {open && (
        <CommandPalette
          onClose={() => setOpen(false)}
        />
      )}
    </CommandPaletteContext.Provider>
  );
}

function useGlobalPaletteShortcuts({
  open,
  setOpen,
}: {
  open: boolean;
  setOpen: (value: boolean | ((prev: boolean) => boolean)) => void;
}) {
  useEffect(() => {
    const handler = (e: KeyboardEvent) => {
      // Cmd-K (macOS) / Ctrl-K (Windows/Linux) — always open/toggle.
      if ((e.metaKey || e.ctrlKey) && e.key.toLowerCase() === "k") {
        e.preventDefault();
        setOpen((prev) => !prev);
        return;
      }

      // ESC — close while open. The palette's own handler also closes
      // but this catches focus edge cases where the palette hasn't
      // mounted its keydown listener yet.
      if (open && e.key === "Escape") {
        e.preventDefault();
        setOpen(false);
        return;
      }

      // "/" — open when focus is outside an editable element so
      // users typing inside an input aren't hijacked. See the test
      // suite for the cases covered.
      if (
        e.key === "/" &&
        !open &&
        !isEditableTarget(e.target) &&
        !e.metaKey &&
        !e.ctrlKey &&
        !e.altKey
      ) {
        e.preventDefault();
        setOpen(true);
      }
    };

    window.addEventListener("keydown", handler);
    return () => window.removeEventListener("keydown", handler);
  }, [open, setOpen]);
}

function isEditableTarget(target: EventTarget | null): boolean {
  if (!(target instanceof HTMLElement)) return false;
  const tag = target.tagName;
  if (tag === "INPUT" || tag === "TEXTAREA" || tag === "SELECT") return true;
  if (target.isContentEditable) return true;
  return false;
}

// ---------------------------------------------------------------------------
// The palette UI itself
// ---------------------------------------------------------------------------

interface PaletteItem {
  id: string;
  label: string;
  keywords: readonly string[];
  group: string;
  description?: string;
  icon?: React.ComponentType<{ className?: string }>;
  onSelect: () => void | Promise<void>;
  routePath?: string;
}

const EXPLORER_ROUTE = "/units";

function CommandPalette({ onClose }: { onClose: () => void }) {
  const routes = useRoutes();
  const actions = usePaletteActions();
  const router = useRouter();
  const pathname = usePathname();
  const explorerSelection = useExplorerSelection();
  const [search, setSearch] = useState("");
  const inputRef = useRef<HTMLInputElement>(null);
  const panelRef = useRef<HTMLDivElement>(null);

  // Flatten routes + actions into a single item list. Grouped for
  // display; the filter still searches across every group.
  const items = useMemo<readonly PaletteItem[]>(
    () =>
      buildItems({
        routes,
        actions,
        navigate: (path) => router.push(path),
        close: onClose,
        teleportToExplorer: (nodeId) => {
          // EXP-cmdk-bridge: when a mounted Explorer is available on
          // /units, dispatch the selection without navigating. In every
          // other case, navigate to /units with the node pre-selected
          // via the URL.
          if (pathname === EXPLORER_ROUTE && explorerSelection.hasListener()) {
            explorerSelection.dispatchSelect(nodeId);
          } else {
            router.push(`${EXPLORER_ROUTE}?node=${encodeURIComponent(nodeId)}`);
          }
        },
      }),
    [routes, actions, router, pathname, explorerSelection, onClose],
  );

  const groups = useMemo(() => {
    const result = new Map<string, PaletteItem[]>();
    for (const item of items) {
      const bucket = result.get(item.group) ?? [];
      bucket.push(item);
      result.set(item.group, bucket);
    }
    return Array.from(result.entries());
  }, [items]);

  // Autofocus the input and restore scroll-lock when the palette
  // closes. Matching the pattern used by `components/ui/dialog.tsx`.
  useEffect(() => {
    inputRef.current?.focus();
    const previous = document.body.style.overflow;
    document.body.style.overflow = "hidden";
    return () => {
      document.body.style.overflow = previous;
    };
  }, []);

  const handleEscape = useCallback(
    (e: React.KeyboardEvent) => {
      if (e.key === "Escape") {
        e.preventDefault();
        onClose();
      }
    },
    [onClose],
  );

  const handleBackdrop = useCallback(
    (e: React.MouseEvent<HTMLDivElement>) => {
      if (e.target === e.currentTarget) onClose();
    },
    [onClose],
  );

  return (
    <div
      className="fixed inset-0 z-50 flex items-start justify-center bg-black/50 p-4 pt-[15vh]"
      onMouseDown={handleBackdrop}
      data-testid="command-palette-backdrop"
    >
      <div
        ref={panelRef}
        role="dialog"
        aria-modal="true"
        aria-label="Command palette"
        className={cn(
          "w-full max-w-lg rounded-lg border border-border bg-card shadow-xl",
          "overflow-hidden",
        )}
      >
        <CommandRoot
          label="Command palette"
          loop
          onKeyDown={handleEscape}
          className="flex flex-col max-h-[70vh]"
        >
          <div className="flex items-center gap-2 border-b border-border px-3">
            <Search className="h-4 w-4 text-muted-foreground" />
            <CommandRoot.Input
              ref={inputRef}
              value={search}
              onValueChange={setSearch}
              placeholder="Search routes and actions…"
              aria-label="Command palette search"
              data-testid="command-palette-input"
              className={cn(
                "flex h-11 w-full bg-transparent py-3 text-sm outline-none",
                "placeholder:text-muted-foreground disabled:opacity-50",
              )}
            />
          </div>
          <CommandRoot.List
            className="max-h-[60vh] overflow-y-auto p-2"
            data-testid="command-palette-list"
          >
            <CommandRoot.Empty className="px-3 py-6 text-center text-sm text-muted-foreground">
              No matches.
            </CommandRoot.Empty>
            {groups.map(([group, entries]) => (
              <CommandRoot.Group
                key={group}
                heading={group}
                className="mb-2 last:mb-0"
                data-testid={`command-palette-group-${group}`}
              >
                <div className="px-2 pb-1 pt-2 text-[10px] font-semibold uppercase tracking-wider text-muted-foreground">
                  {group}
                </div>
                {entries.map((item) => {
                  const Icon = item.icon;
                  return (
                    <CommandRoot.Item
                      key={item.id}
                      value={item.id}
                      keywords={[item.label, ...item.keywords]}
                      onSelect={() => {
                        void item.onSelect();
                      }}
                      data-testid={`command-palette-item-${item.id}`}
                      className={cn(
                        "flex cursor-pointer items-center gap-3 rounded-md px-2 py-2 text-sm",
                        "aria-selected:bg-accent aria-selected:text-accent-foreground",
                        "data-[selected=true]:bg-accent data-[selected=true]:text-accent-foreground",
                      )}
                    >
                      {Icon ? (
                        <Icon className="h-3.5 w-3.5 text-muted-foreground" />
                      ) : (
                        <ArrowRight className="h-3.5 w-3.5 text-muted-foreground" />
                      )}
                      <span className="flex-1">
                        <span className="block">{item.label}</span>
                        {item.description && (
                          <span className="block text-xs text-muted-foreground">
                            {item.description}
                          </span>
                        )}
                      </span>
                      {item.routePath && (
                        <span className="text-[10px] text-muted-foreground">
                          {item.routePath}
                        </span>
                      )}
                    </CommandRoot.Item>
                  );
                })}
              </CommandRoot.Group>
            ))}
          </CommandRoot.List>
          <div className="border-t border-border px-3 py-2 text-[11px] text-muted-foreground">
            <span>↑↓ to navigate</span>
            <span className="mx-2">·</span>
            <span>↵ to run</span>
            <span className="mx-2">·</span>
            <span>esc to close</span>
          </div>
        </CommandRoot>
      </div>
    </div>
  );
}

function buildItems({
  routes,
  actions,
  navigate,
  close,
  teleportToExplorer,
}: {
  routes: readonly RouteEntry[];
  actions: readonly PaletteAction[];
  navigate: (path: string) => void;
  close: () => void;
  teleportToExplorer: (nodeId: string) => void;
}): readonly PaletteItem[] {
  const result: PaletteItem[] = [];

  for (const route of routes) {
    result.push({
      id: `route:${route.path}`,
      label: route.label,
      keywords: [route.path, ...(route.keywords ?? [])],
      group: groupLabelForRoute(route),
      description: route.description,
      icon: route.icon,
      routePath: route.path,
      onSelect: () => {
        close();
        navigate(route.path);
      },
    });
  }

  for (const action of actions) {
    const nodeId = action.explorerNodeId;
    result.push({
      id: `action:${action.id}`,
      label: action.label,
      keywords: action.keywords ?? [],
      group: action.section ?? "Actions",
      description: action.description,
      icon: action.icon,
      routePath: action.href,
      onSelect: () => {
        close();
        if (nodeId !== undefined) {
          // Node-kind action: teleport into the Explorer (or navigate
          // there first if the Explorer isn't mounted).
          teleportToExplorer(nodeId);
          return;
        }
        if (action.onSelect) {
          void action.onSelect();
          return;
        }
        if (action.href) {
          navigate(action.href);
        }
      },
    });
  }

  return result;
}

function groupLabelForRoute(route: RouteEntry): string {
  if (route.navSection === "settings") return "Settings";
  return "Navigate";
}
