"use client";

import { Search } from "lucide-react";
import { useCallback, useMemo, useState } from "react";

import { cn } from "@/lib/utils";

import {
  findIndex,
  tabsFor,
  type TabName,
  type TreeNode,
} from "./aggregate";
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
 * Canonical `/units` route per the v2 plan §3 (Information Architecture).
 *
 * The explorer is fully controllable via `selectedId` + `tab` so the
 * `EXP-route` issue can wire it to URL-driven state (`?node=…&tab=…`)
 * without forking. When `selectedId` is omitted the component manages
 * selection internally so dialogs / mock mounts keep working without
 * router wiring.
 *
 * Data shape, search semantics, and tab content come from sibling
 * issues:
 *   - `EXP-search`        adds the live tree-filter search.
 *   - `EXP-route`         wires URL ⇄ component state.
 *   - `EXP-tab-*`         registers per-tab content via the registry in
 *                         `units/tabs/index.ts`.
 *   - `V21-tree-keyboard` adds arrow-key navigation to the tree.
 *
 * Foundation `FOUND-explorer` ships the layout, ARIA tree roles, the
 * detail-pane chrome, and a placeholder body so the surface is testable
 * end-to-end before the data + tabs land.
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

  const setTab = useCallback(
    (next: TabName) => {
      setTabByNode((prev) =>
        prev[selectedId] === next ? prev : { ...prev, [selectedId]: next },
      );
      onTabChange?.(selectedId, next);
    },
    [selectedId, onTabChange],
  );

  // Search input lives in component state for now (the FOUND PR ships the
  // affordance with no filtering wired). `EXP-search` replaces this with
  // a fuzzy-match filter that hides non-matching nodes while keeping
  // ancestors visible so the tree stays navigable mid-search.
  const [query, setQuery] = useState("");

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
          <UnitTree
            tree={tree}
            selectedId={selectedId}
            onSelect={setSelected}
          />
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
