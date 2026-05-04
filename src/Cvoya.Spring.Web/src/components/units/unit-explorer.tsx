"use client";

import { Search } from "lucide-react";
import { useCallback, useEffect, useMemo, useState } from "react";

import { cn } from "@/lib/utils";

import {
  childrenOf,
  filterTree,
  findIndex,
  flattenTree,
  tabsFor,
  type TabName,
  type TreeNode,
} from "./aggregate";
import { useExplorerSelection } from "./explorer-selection-context";
import { DetailPane } from "./unit-detail-pane";
import { UnitTree } from "./unit-tree";

interface UnitExplorerProps {
  /**
   * Root of the tree to explore. `<UnitExplorer>` does not synthesize a
   * tenant root on its own — the route ships an explicit root node
   * (typically a synthesized `kind: "Tenant"` node) so this component
   * can stay agnostic of the source of truth.
   */
  tree: TreeNode;
  /**
   * Controlled selection. Pass the URL-driven node id; the explorer
   * dispatches changes via {@link onSelectNode}. Defaults to the tree
   * root when undefined or when the id no longer maps to a node.
   */
  selectedId?: string;
  onSelectNode?: (id: string) => void;
  /** Controlled active tab. Falls back to the kind's first tab. */
  tab?: TabName;
  onTabChange?: (selectedId: string, tab: TabName) => void;
  className?: string;
}

/**
 * Two-pane Explorer surface — tree on the left, detail on the right.
 * The canonical `/units` route mounts this component.
 *
 * The explorer is fully controllable via `selectedId` + `tab` so callers
 * can wire URL-driven state (`?node=…&tab=…`) without forking. When
 * `selectedId` is omitted the component manages selection internally so
 * dialogs / mock mounts keep working without router wiring.
 *
 * Per-tab content is registered via the shared registry in
 * `units/tabs/index.ts`; an unregistered `(kind, tab)` pair falls through
 * to `<TabPlaceholder>` so the surface is testable end-to-end even before
 * every tab body lands.
 */
export function UnitExplorer({
  tree,
  selectedId: controlledSelectedId,
  onSelectNode: onSelectProp,
  tab: controlledTab,
  onTabChange,
  className,
}: UnitExplorerProps) {
  const { byId } = useMemo(() => findIndex(tree), [tree]);

  const [internalSelectedId, setInternalSelectedId] = useState<string>(
    () => tree.id,
  );
  const selectedId = controlledSelectedId ?? internalSelectedId;
  // Fall back to the tree root if the URL points at a node that no longer
  // exists (e.g. a unit was deleted between two visits).
  const entry = byId[selectedId] ?? byId[tree.id];
  const node = entry.node;
  const path = entry.path;

  const [tabByNode, setTabByNode] = useState<Record<string, TabName>>({});
  const tab =
    controlledTab ?? tabByNode[selectedId] ?? tabsFor(node.kind)[0];

  const setSelected = useCallback(
    (id: string) => {
      onSelectProp?.(id);
      if (controlledSelectedId === undefined) setInternalSelectedId(id);
    },
    [controlledSelectedId, onSelectProp],
  );

  // Register `setSelected` with the shell-level selection bridge so the
  // command palette can teleport the Explorer without a router
  // round-trip. The bridge is a no-op outside an
  // `<ExplorerSelectionProvider>`, so isolated component tests that
  // don't wrap the provider still work.
  const { registerListener } = useExplorerSelection();
  useEffect(() => registerListener(setSelected), [registerListener, setSelected]);

  const setTab = useCallback(
    (next: TabName) => {
      setTabByNode((prev) =>
        prev[selectedId] === next ? prev : { ...prev, [selectedId]: next },
      );
      onTabChange?.(selectedId, next);
    },
    [selectedId, onTabChange],
  );

  // Case-insensitive substring filter over `name`. Picked over fuzzy
  // matching for v0.1 — predictable, dependency-free, easy to upgrade
  // later (#1624). Matches against both units and agents (the input
  // placeholder says "Search units & agents…"). The filter is purely
  // client-side over already-fetched data; the API isn't involved.
  const [query, setQuery] = useState("");
  const trimmedQuery = query.trim();
  const isFiltering = trimmedQuery.length > 0;

  const { filteredTree, expandedForFilter } = useMemo(() => {
    if (!isFiltering) {
      return { filteredTree: tree, expandedForFilter: undefined };
    }
    const { tree: pruned } = filterTree(tree, trimmedQuery);
    if (!pruned) {
      return { filteredTree: null, expandedForFilter: undefined };
    }
    // Force every surviving branch open so ancestors of matches are
    // immediately visible — the whole point of the ancestor-preservation
    // pass is that operators see the path to a hit without clicking.
    const expanded: Record<string, boolean> = {};
    for (const { node } of flattenTree(pruned)) {
      if (childrenOf(node).length > 0) expanded[node.id] = true;
    }
    return { filteredTree: pruned, expandedForFilter: expanded };
  }, [tree, trimmedQuery, isFiltering]);

  return (
    <div
      data-testid="unit-explorer"
      className={cn(
        "grid h-full min-h-0 grid-cols-[280px_minmax(0,1fr)] divide-x divide-border",
        className,
      )}
    >
      <aside
        aria-label="Unit tree"
        className="flex h-full min-h-0 flex-col bg-card"
      >
        <div className="border-b border-border p-2">
          <div className="relative">
            <Search
              aria-hidden="true"
              className="pointer-events-none absolute left-2 top-1/2 h-3.5 w-3.5 -translate-y-1/2 text-muted-foreground"
            />
            <input
              type="search"
              value={query}
              onChange={(e) => setQuery(e.target.value)}
              placeholder="Search units & agents…"
              aria-label="Search units & agents"
              data-testid="unit-explorer-search"
              className="h-8 w-full rounded-md border border-input bg-background pl-7 pr-2 text-xs placeholder:text-muted-foreground focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring"
            />
          </div>
        </div>
        <div className="flex-1 overflow-auto p-1">
          {filteredTree ? (
            <UnitTree
              // Remount when the filter shape changes so the tree's
              // internal `expanded` state picks up the freshly-computed
              // `defaultExpanded`. Without this, switching between
              // queries would leave stale collapse state from a prior
              // pass — operators would see a hit but not the branch
              // that holds it.
              key={isFiltering ? `filter:${trimmedQuery}` : "no-filter"}
              tree={filteredTree}
              selectedId={selectedId}
              onSelect={setSelected}
              defaultExpanded={expandedForFilter}
            />
          ) : (
            <p
              data-testid="unit-explorer-no-matches"
              className="px-2 py-3 text-xs text-muted-foreground"
            >
              No units or agents match &ldquo;{trimmedQuery}&rdquo;.
            </p>
          )}
        </div>
      </aside>
      <DetailPane
        node={node}
        path={path}
        tab={tab}
        onTabChange={setTab}
        onSelectNode={setSelected}
      />
    </div>
  );
}
