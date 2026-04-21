"use client";

import { Bot, ChevronRight, Globe, Layers } from "lucide-react";
import { createElement, useEffect, useId } from "react";

import { Badge } from "@/components/ui/badge";
import { cn } from "@/lib/utils";

import {
  type NodeKind,
  type NodeStatus,
  type TabName,
  type TreeNode,
  tabsFor,
} from "./aggregate";
import { TabPlaceholder } from "./tab-placeholder";
import { lookupTab } from "./tabs";

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
 * derives the strip from `tabsFor(node.kind)` so adding a tab to the
 * source-of-truth catalog (`UNIT_TABS`, etc.) automatically widens the
 * strip everywhere the pane renders.
 *
 * If the active `tab` prop is not in the kind's catalog (e.g. a stale URL
 * fragment that says `tab=Skills` while the user navigated to a unit),
 * the pane snaps to the kind's first tab via the `useEffect` below. This
 * keeps the URL → state sync one-directional: the URL drives `tab`,
 * the pane drives the URL via `onTabChange`.
 */
export function DetailPane({
  node,
  path,
  tab,
  onTabChange,
  onSelectNode,
}: DetailPaneProps) {
  const tabs = tabsFor(node.kind);
  const isValidTab = tabs.includes(tab);
  // Stable, hydration-safe prefix per DetailPane mount. Combined with the
  // tab slug it yields the unique `id`s the WAI-ARIA tabs pattern requires
  // so a screen reader can tie the tab button to the panel it controls.
  const idPrefix = useId();

  useEffect(() => {
    if (!isValidTab) onTabChange(tabs[0]);
  }, [isValidTab, tabs, onTabChange]);

  const activeTab: TabName = isValidTab ? tab : tabs[0];
  const tabId = (t: TabName) => `${idPrefix}-tab-${tabSlug(t)}`;
  const panelId = `${idPrefix}-panel-${tabSlug(activeTab)}`;

  // The registry returns `null` when no per-tab component has been
  // registered (the foundation PR ships zero registrations on purpose).
  // Fall through to the generic `<TabPlaceholder>` so the pane still
  // renders sensibly.
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
        <Breadcrumb path={path} onSelect={onSelectNode} />
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
        </div>
        <TabStrip
          tabs={tabs}
          active={activeTab}
          onPick={onTabChange}
          tabId={tabId}
          panelId={panelId}
        />
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
}: {
  tabs: readonly TabName[];
  active: TabName;
  onPick: (tab: TabName) => void;
  tabId: (tab: TabName) => string;
  panelId: string;
}) {
  return (
    <div
      role="tablist"
      aria-label="Detail tabs"
      data-testid="detail-tabstrip"
      className="-mb-3 mt-3 flex items-center gap-1 overflow-x-auto"
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
      return "bg-warning";
    case "error":
      return "bg-destructive";
    case "paused":
      return "bg-warning/70";
    case "stopped":
    default:
      return "bg-debug";
  }
}
