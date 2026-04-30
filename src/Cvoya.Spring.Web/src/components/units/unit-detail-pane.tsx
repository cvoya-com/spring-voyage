"use client";

import { Bot, Check, ChevronRight, Copy, Globe, Layers } from "lucide-react";
import {
  createElement,
  type KeyboardEvent,
  useCallback,
  useEffect,
  useId,
  useState,
} from "react";

import { Badge } from "@/components/ui/badge";
import { cn } from "@/lib/utils";

import {
  type NodeKind,
  type NodeStatus,
  overflowTabsFor,
  type TabName,
  tabsFor,
  type TreeNode,
  visibleTabsFor,
} from "./aggregate";
import { TabPlaceholder } from "./tab-placeholder";
import { lookupTab } from "./tabs";
import { UnitPaneActions } from "./unit-pane-actions";

function tabSlug(tab: TabName): string {
  return tab.toLowerCase();
}

interface DetailPaneProps {
  node: TreeNode;
  path: TreeNode[];
  /** Active tab — controlled by the parent so the URL/router stays the source of truth. */
  tab: TabName;
  onTabChange: (tab: TabName) => void;
  onSelectNode: (id: string) => void;
}

/**
 * Right-hand pane of `<UnitExplorer>`: header + breadcrumb + tab strip +
 * registered-tab content (or a {@link TabPlaceholder} fallback).
 *
 * The pane is intentionally dumb about *which* tab catalog applies — it
 * derives the strip from `visibleTabsFor(node.kind)` (plus an overflow
 * strip from `overflowTabsFor(node.kind)` when the kind has overflow
 * tabs) so adding a tab to the source-of-truth catalog (`UNIT_TABS`,
 * etc.) automatically widens the strip everywhere the pane renders.
 *
 * Overflow tabs (today: Unit's `Config`) render as a second tablist
 * after a visual separator. They're still `role="tab"` buttons wired to
 * the same `onTabChange` callback, so activating one behaves identically
 * to activating a visible tab — the URL carries the same `tab` value
 * either way, and a stale `?tab=Config` URL snaps the pane to the
 * overflow tab.
 *
 * If the active `tab` prop is not in the kind's catalog (e.g. a stale URL
 * fragment that says `tab=Skills` while the user navigated to a unit),
 * the pane snaps to the kind's first visible tab via the `useEffect`
 * below. This keeps the URL → state sync one-directional: the URL
 * drives `tab`, the pane drives the URL via `onTabChange`.
 */
export function DetailPane({
  node,
  path,
  tab,
  onTabChange,
  onSelectNode,
}: DetailPaneProps) {
  const visibleTabs = visibleTabsFor(node.kind);
  const overflowTabs = overflowTabsFor(node.kind);
  const allTabs = tabsFor(node.kind);
  const isValidTab = allTabs.includes(tab);
  // Stable, hydration-safe prefix per DetailPane mount. Combined with the
  // tab slug it yields the unique `id`s the WAI-ARIA tabs pattern requires
  // so a screen reader can tie the tab button to the panel it controls.
  const idPrefix = useId();

  useEffect(() => {
    if (!isValidTab) onTabChange(visibleTabs[0]);
  }, [isValidTab, visibleTabs, onTabChange]);

  const activeTab: TabName = isValidTab ? tab : visibleTabs[0];
  const tabId = (t: TabName) => `${idPrefix}-tab-${tabSlug(t)}`;
  const panelId = `${idPrefix}-panel-${tabSlug(activeTab)}`;

  // The registry returns `null` when no per-tab component has been
  // registered; fall through to the generic `<TabPlaceholder>` so the
  // pane still renders sensibly.
  //
  // `createElement` is used instead of `<TabComponent />` because the
  // `react-hooks/static-components` lint rule disallows capital-cased
  // component aliases inside render bodies; the imperative form is
  // identical at runtime.
  const tabComponent = lookupTab(node.kind, activeTab);

  return (
    <section
      data-testid="unit-detail-pane"
      className="flex h-full flex-col overflow-hidden bg-background"
    >
      <header className="border-b border-border px-6 pb-3 pt-4">
        <div className="flex items-center gap-2">
          <Breadcrumb path={path} onSelect={onSelectNode} />
          <CopyAddressButton address={addressFor(node, path)} />
        </div>
        <div className="mt-2 flex items-center gap-3">
          <span
            aria-hidden="true"
            className={cn(
              "h-2.5 w-2.5 shrink-0 rounded-full",
              statusDotClass(node.status),
            )}
            data-testid="detail-status-dot"
            data-status={node.status}
          />
          <KindIcon
            kind={node.kind}
            className="h-5 w-5 shrink-0 text-muted-foreground"
          />
          <h1 className="truncate text-lg font-semibold">{node.name}</h1>
          <Badge variant="outline" className="capitalize">
            {node.status}
          </Badge>
          {node.kind === "Agent" && node.role ? (
            <Badge variant="secondary">{node.role}</Badge>
          ) : null}
          <div className="ml-auto">
            <UnitPaneActions node={node} />
          </div>
        </div>
        <div className="-mb-3 mt-3 flex items-center gap-2 overflow-x-auto">
          <TabStrip
            tabs={visibleTabs}
            active={activeTab}
            onPick={onTabChange}
            tabId={tabId}
            panelId={panelId}
            ariaLabel="Detail tabs"
            testId="detail-tabstrip"
          />
          {overflowTabs.length > 0 ? (
            <>
              <span
                aria-hidden="true"
                data-testid="detail-tabstrip-separator"
                className="h-5 w-px shrink-0 bg-border"
              />
              <TabStrip
                tabs={overflowTabs}
                active={activeTab}
                onPick={onTabChange}
                tabId={tabId}
                panelId={panelId}
                ariaLabel="More detail tabs"
                testId="detail-tabstrip-overflow"
              />
            </>
          ) : null}
        </div>
      </header>
      <div
        role="tabpanel"
        id={panelId}
        aria-labelledby={tabId(activeTab)}
        tabIndex={0}
        className="flex-1 overflow-auto p-6 focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring"
      >
        {tabComponent ? (
          createElement(tabComponent, { node, path })
        ) : (
          <TabPlaceholder tab={activeTab} kind={node.kind} />
        )}
      </div>
    </section>
  );
}

/**
 * Canonical address for a tree node — what gets copied to the clipboard
 * by `<CopyAddressButton>` and what the backend matches against (#1070).
 *
 * The synthesized tenant root already ships an id of the form
 * `tenant://<id>` (see `validate-tenant-tree.test.ts` and the wire shape
 * documented in `aggregate.ts`); units and agents land bare. Treat any
 * id that already carries a known scheme prefix as canonical so a future
 * server-side reshape that pushes prefixes onto every kind doesn't
 * double-prefix here.
 *
 * #1200: for agent nodes the address now includes the full unit-path
 * prefix so the copied identity is globally unique. The `path` argument
 * is the ancestor chain supplied by `findIndex` — e.g.
 * `[TenantNode, UnitNode("engineering-team"), AgentNode("ada")]` yields
 * `agent://engineering-team/ada`. When `path` is omitted or contains no
 * Unit ancestors the address falls back to `agent://<name>` (same as
 * before) so callers that do not have a path context are unaffected.
 */
export function addressFor(node: TreeNode, path?: TreeNode[]): string {
  const id = node.id;
  const SCHEMES = ["tenant://", "unit://", "agent://"];
  if (SCHEMES.some((s) => id.startsWith(s))) return id;
  switch (node.kind) {
    case "Tenant":
      return `tenant://${id}`;
    case "Unit":
      return `unit://${id}`;
    case "Agent": {
      // Build the full path by collecting the names of Unit ancestors
      // from the breadcrumb path (Tenant nodes are excluded — they are
      // not part of the address). This produces the globally-unique
      // `agent://<unit>/…/<agent>` form the issue requires.
      if (path && path.length > 0) {
        const unitSegments = path
          .filter((n) => n.kind === "Unit")
          .map((n) => n.id);
        if (unitSegments.length > 0) {
          return `agent://${unitSegments.join("/")}/${id}`;
        }
      }
      return `agent://${id}`;
    }
  }
}

/**
 * Icon-only "copy address" button mirroring the dashboard pattern
 * (`app/page.tsx` `dashboard-copy-address`): swap to a check glyph for
 * ~1.5 s on success, swallow clipboard failures (insecure context /
 * permission denied) since the surface has no toast bus to dispatch to.
 *
 * Lives in the breadcrumb row so the address tracks the active selection
 * — Cmd-K teleport, tree click, breadcrumb click, deep-link all keep
 * the copy target in sync without an extra hand-wired ref.
 */
function CopyAddressButton({ address }: { address: string }) {
  const [copied, setCopied] = useState(false);
  const handleCopy = async () => {
    try {
      await navigator.clipboard.writeText(address);
      setCopied(true);
      window.setTimeout(() => setCopied(false), 1500);
    } catch {
      // Clipboard can fail on insecure origins or when the user denies
      // permission. Silent — same posture as `<DashboardHeader>`.
    }
  };
  return (
    <button
      type="button"
      onClick={handleCopy}
      aria-label={copied ? "Address copied" : `Copy address ${address}`}
      title={address}
      data-testid="detail-copy-address"
      className="inline-flex h-6 w-6 shrink-0 items-center justify-center rounded text-muted-foreground transition-colors hover:bg-accent hover:text-foreground focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring"
    >
      {copied ? (
        <Check className="h-3.5 w-3.5" aria-hidden="true" />
      ) : (
        <Copy className="h-3.5 w-3.5" aria-hidden="true" />
      )}
    </button>
  );
}

function Breadcrumb({
  path,
  onSelect,
}: {
  path: TreeNode[];
  onSelect: (id: string) => void;
}) {
  return (
    <nav aria-label="Breadcrumb" className="flex items-center gap-1 text-xs">
      {path.map((node, i) => {
        const last = i === path.length - 1;
        return (
          <div key={node.id} className="flex items-center gap-1">
            {i > 0 ? (
              <ChevronRight
                aria-hidden="true"
                className="h-3 w-3 shrink-0 text-muted-foreground"
              />
            ) : null}
            <button
              type="button"
              onClick={() => onSelect(node.id)}
              aria-current={last ? "page" : undefined}
              data-testid={`detail-crumb-${node.id}`}
              className={cn(
                "rounded px-1 py-0.5 transition-colors hover:bg-accent",
                last
                  ? "font-medium text-foreground"
                  : "text-muted-foreground hover:text-foreground",
              )}
            >
              {node.name}
            </button>
          </div>
        );
      })}
    </nav>
  );
}

function TabStrip({
  tabs,
  active,
  onPick,
  tabId,
  panelId,
  ariaLabel,
  testId,
}: {
  tabs: readonly TabName[];
  active: TabName;
  onPick: (tab: TabName) => void;
  tabId: (tab: TabName) => string;
  panelId: string;
  ariaLabel: string;
  testId: string;
}) {
  // Automatic-activation keyboard flavour per the WAI-ARIA APG tabs
  // pattern (https://www.w3.org/WAI/ARIA/apg/patterns/tabs/):
  //   • ←/→ focus + activate the adjacent tab (wraps at the ends).
  //   • Home/End focus + activate the first/last tab.
  //   • Enter/Space are no-ops here (automatic activation already
  //     selected on focus); we still intercept them so screen readers
  //     that announce via Enter don't double-fire the click path.
  //   • Tab/Shift+Tab move focus out of the tablist via the existing
  //     roving tabIndex prep (inactive tabs carry `-1`).
  // Activating a tab by keyboard goes through the same `onPick` callback
  // as clicking, so the URL/state round-trip is identical.
  const handleKeyDown = useCallback(
    (e: KeyboardEvent<HTMLDivElement>) => {
      const idx = tabs.indexOf(active);
      if (idx === -1) return;
      const n = tabs.length;
      switch (e.key) {
        case "ArrowLeft": {
          e.preventDefault();
          onPick(tabs[(idx - 1 + n) % n]);
          return;
        }
        case "ArrowRight": {
          e.preventDefault();
          onPick(tabs[(idx + 1) % n]);
          return;
        }
        case "Home": {
          e.preventDefault();
          onPick(tabs[0]);
          return;
        }
        case "End": {
          e.preventDefault();
          onPick(tabs[n - 1]);
          return;
        }
        case "Enter":
        case " ": {
          // Automatic activation: focus already selected. Swallow so the
          // browser doesn't click the tab button a second time (which is
          // a no-op but causes noisy screen-reader announcements).
          e.preventDefault();
          return;
        }
      }
    },
    [tabs, active, onPick],
  );

  return (
    <div
      role="tablist"
      aria-label={ariaLabel}
      data-testid={testId}
      onKeyDown={handleKeyDown}
      className="flex items-center gap-1"
    >
      {tabs.map((t) => {
        const selected = t === active;
        return (
          <button
            key={t}
            id={tabId(t)}
            role="tab"
            aria-selected={selected}
            aria-controls={selected ? panelId : undefined}
            tabIndex={selected ? 0 : -1}
            type="button"
            data-testid={`detail-tab-${t.toLowerCase()}`}
            onClick={() => onPick(t)}
            className={cn(
              "rounded-md px-3 py-2 text-xs font-medium transition-colors focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring",
              selected
                ? "bg-primary/10 text-primary"
                : "text-muted-foreground hover:bg-accent hover:text-foreground",
            )}
          >
            {t}
          </button>
        );
      })}
    </div>
  );
}

/**
 * Static, lint-friendly icon picker. See the matching helper in
 * `unit-tree.tsx` — same pattern, different sizing defaults at the call site.
 */
function KindIcon({
  kind,
  className,
}: {
  kind: NodeKind;
  className?: string;
}) {
  switch (kind) {
    case "Tenant":
      return <Globe aria-hidden="true" className={className} />;
    case "Agent":
      return <Bot aria-hidden="true" className={className} />;
    case "Unit":
    default:
      return <Layers aria-hidden="true" className={className} />;
  }
}

function statusDotClass(status: NodeStatus): string {
  switch (status) {
    case "running":
      return "bg-success";
    case "starting":
    case "stopping":
    case "validating":
      return "bg-warning";
    case "error":
      return "bg-destructive";
    case "paused":
      return "bg-warning/70";
    case "draft":
    case "stopped":
    default:
      return "bg-debug";
  }
}
