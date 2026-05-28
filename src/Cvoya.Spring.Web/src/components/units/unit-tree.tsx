"use client";

import { Bot, Globe, Layers, User } from "lucide-react";
import {
  type KeyboardEvent,
  useCallback,
  useEffect,
  useMemo,
  useRef,
  useState,
} from "react";

import { useIssueCounts } from "@/lib/api/queries";
import { cn } from "@/lib/utils";

import {
  aggregate,
  childrenOf,
  flattenTree,
  type NodeKind,
  type NodeStatus,
  type TreeNode,
} from "./aggregate";

interface UnitTreeProps {
  /**
   * Root of the tree to render. For the portal this is the synthesized
   * tenant root served by `GET /api/v1/tenant/tree`; the tree is rendered
   * verbatim with no frontend-side synthesis.
   */
  tree: TreeNode;
  /** Currently-selected node id; controlled by the parent (URL-driven). */
  selectedId: string;
  onSelect: (id: string) => void;
  /**
   * Initial expansion state, keyed on node id. Defaults to expanding only
   * the tree root so the operator sees the first level immediately.
   */
  defaultExpanded?: Record<string, boolean>;
  className?: string;
  /**
   * Suppress the per-branch agent-count badge. Use on surfaces where the
   * count is misleading (e.g. the Interactions scope picker, where
   * operators read the badge as an interaction count). Defaults to false
   * so the existing Explorer surface keeps the badge.
   */
  hideCountBadge?: boolean;
}

/**
 * Static-ARIA tree following the WAI-ARIA Authoring Practices Guide
 * (https://www.w3.org/WAI/ARIA/apg/patterns/treeview/).
 *
 * The container carries `role="tree"`. Each row is a `treeitem` with
 * `aria-selected`, `aria-level`, and (for branches) `aria-expanded`. Click
 * on the row body selects the node; click on the twisty toggles
 * expansion without changing selection — operators can survey a subtree
 * without losing context.
 *
 * Keyboard navigation follows the APG treeview pattern: arrow keys walk
 * visible rows, Home/End jump to extremes, →/← expand/collapse (or
 * descend/ascend for already-open or leaf rows), Enter/Space select, and
 * printable keys drive a type-ahead match over visible labels. Focus is
 * managed via the roving-tabindex pattern so a single Tab stop enters
 * the tree and arrow keys take over from there.
 */
export function UnitTree({
  tree,
  selectedId,
  onSelect,
  defaultExpanded,
  className,
  hideCountBadge,
}: UnitTreeProps) {
  const [expanded, setExpanded] = useState<Record<string, boolean>>(
    () => defaultExpanded ?? { [tree.id]: true },
  );
  const toggle = useCallback(
    (id: string) =>
      setExpanded((prev) => ({ ...prev, [id]: !prev[id] })),
    [],
  );

  // The row that currently carries `tabIndex=0` — roving-tabindex anchor.
  // Defaults to the controlled `selectedId` so the very first focus lands on
  // the selected row, but tracks arrow-key movement separately so operators
  // can survey the tree without shifting selection.
  const [focusedId, setFocusedId] = useState<string>(selectedId);

  // #1704: re-anchor the tabstop when `selectedId` changes externally (URL
  // navigation, Cmd-K teleport, dashboard deep-link). Without this, keyboard
  // Enter would re-select the previously-focused row, making selection appear
  // to "snap back" to the old node.
  useEffect(() => {
    setFocusedId(selectedId);
  }, [selectedId]);

  const containerRef = useRef<HTMLDivElement>(null);
  // Short-lived type-ahead buffer: printable keys append; ~500 ms of
  // silence resets.
  const typeAheadRef = useRef<{ buffer: string; until: number }>({
    buffer: "",
    until: 0,
  });

  // Visible rows in top-down DOM order — recomputed whenever the tree or
  // expansion state changes. Keyboard handlers index into this array.
  const visibleRows = useMemo(() => {
    const out: TreeNode[] = [];
    const walk = (node: TreeNode) => {
      out.push(node);
      if (expanded[node.id]) {
        for (const child of childrenOf(node)) walk(child);
      }
    };
    walk(tree);
    return out;
  }, [tree, expanded]);

  // #2183: derive every unit/agent subject in the tree (regardless of
  // expansion state — collapsed branches still need badges so a buried
  // failure paints the parent row). Use the canonical
  // `(scheme, definitionId)` pair so the batch endpoint can index-bash
  // on Guid columns rather than slug lookups.
  const issueSubjects = useMemo(() => {
    const out: string[] = [];
    for (const { node } of flattenTree(tree)) {
      if (node.kind === "Tenant") continue;
      if (!node.definitionId) continue;
      const scheme = node.kind === "Unit" ? "unit" : "agent";
      out.push(`${scheme}:${node.definitionId}`);
    }
    return out;
  }, [tree]);
  const issueCountsQuery = useIssueCounts(issueSubjects);
  const issueCountsByDefinitionId = useMemo(() => {
    const map = new Map<string, { errorCount: number; warningCount: number }>();
    for (const entry of issueCountsQuery.data?.counts ?? []) {
      map.set(entry.subjectId, {
        errorCount: entry.errorCount,
        warningCount: entry.warningCount,
      });
    }
    return map;
  }, [issueCountsQuery.data]);

  // `parentOf[childId] = parentId` — lets ← find the parent row in O(1).
  const parentOf = useMemo(() => {
    const out: Record<string, string> = {};
    for (const { node, path } of flattenTree(tree)) {
      if (path.length >= 2) {
        out[node.id] = path[path.length - 2].id;
      }
    }
    return out;
  }, [tree]);

  const moveFocusTo = useCallback((id: string) => {
    setFocusedId(id);
    // `data-testid` is the cheapest stable hook back to the row element —
    // avoids threading refs through every recursive render.
    const row = containerRef.current?.querySelector<HTMLElement>(
      `[data-testid="tree-row-${id}"]`,
    );
    row?.focus();
  }, []);

  const handleKeyDown = useCallback(
    (e: KeyboardEvent<HTMLDivElement>) => {
      // Resolve the current row from the DOM-focused element (falling back
      // to the state-tracked focused id). This keeps keyboard handling
      // correct when focus has been moved by user code (e.g. a router
      // teleport) ahead of React re-rendering.
      const target = e.target as HTMLElement | null;
      const focusedRow = target?.closest<HTMLElement>('[data-testid^="tree-row-"]');
      const currentIdFromDom = focusedRow?.getAttribute("data-testid")?.replace(
        /^tree-row-/,
        "",
      );
      const currentId = currentIdFromDom ?? focusedId;
      const idx = visibleRows.findIndex((n) => n.id === currentId);
      if (idx === -1) return;
      const current = visibleRows[idx];
      const isOpen = !!expanded[current.id];
      const hasChildren = childrenOf(current).length > 0;

      switch (e.key) {
        case "ArrowDown": {
          e.preventDefault();
          const next = visibleRows[idx + 1];
          if (next) moveFocusTo(next.id);
          return;
        }
        case "ArrowUp": {
          e.preventDefault();
          const prev = visibleRows[idx - 1];
          if (prev) moveFocusTo(prev.id);
          return;
        }
        case "ArrowRight": {
          e.preventDefault();
          if (hasChildren && !isOpen) {
            // Closed branch → expand, focus stays.
            toggle(current.id);
          } else if (hasChildren && isOpen) {
            // Open branch → descend to the first child.
            const first = childrenOf(current)[0];
            if (first) moveFocusTo(first.id);
          }
          // Leaf → no-op per APG.
          return;
        }
        case "ArrowLeft": {
          e.preventDefault();
          if (hasChildren && isOpen) {
            // Open branch → collapse, focus stays.
            toggle(current.id);
          } else {
            // Leaf or closed branch → ascend to the parent (if any).
            const parent = parentOf[current.id];
            if (parent) moveFocusTo(parent);
          }
          return;
        }
        case "Home": {
          e.preventDefault();
          const first = visibleRows[0];
          if (first) moveFocusTo(first.id);
          return;
        }
        case "End": {
          e.preventDefault();
          const last = visibleRows[visibleRows.length - 1];
          if (last) moveFocusTo(last.id);
          return;
        }
        case "Enter":
        case " ": {
          e.preventDefault();
          onSelect(current.id);
          return;
        }
        default: {
          // Type-ahead: single printable character, no modifiers. Matches
          // the APG rule "find the next visible row whose label starts
          // with the accumulated buffer".
          if (
            e.key.length === 1 &&
            !e.ctrlKey &&
            !e.metaKey &&
            !e.altKey &&
            /\S/.test(e.key)
          ) {
            e.preventDefault();
            const now = Date.now();
            const state = typeAheadRef.current;
            const buffer =
              (now < state.until ? state.buffer : "") + e.key.toLowerCase();
            typeAheadRef.current = { buffer, until: now + 500 };

            // Start searching from the row *after* the current one so
            // repeating the same key cycles through matches.
            const startAfter = idx;
            const n = visibleRows.length;
            for (let step = 1; step <= n; step++) {
              const candidate = visibleRows[(startAfter + step) % n];
              if (candidate.name.toLowerCase().startsWith(buffer)) {
                moveFocusTo(candidate.id);
                return;
              }
            }
          }
        }
      }
    },
    [focusedId, visibleRows, expanded, parentOf, toggle, moveFocusTo, onSelect],
  );

  return (
    <div
      ref={containerRef}
      role="tree"
      aria-label="Units & agents"
      data-testid="unit-tree"
      onKeyDown={handleKeyDown}
      className={cn("flex flex-col gap-px py-1", className)}
    >
      <TreeRow
        node={tree}
        depth={0}
        expanded={expanded}
        onToggle={toggle}
        onSelect={(id) => {
          // Clicking a row should anchor the tabstop there too, so a
          // subsequent Tab-in / arrow key resumes from the clicked row.
          setFocusedId(id);
          onSelect(id);
        }}
        selectedId={selectedId}
        focusedId={focusedId}
        issueCountsByDefinitionId={issueCountsByDefinitionId}
        hideCountBadge={hideCountBadge}
      />
    </div>
  );
}

interface TreeRowProps {
  node: TreeNode;
  depth: number;
  expanded: Record<string, boolean>;
  onToggle: (id: string) => void;
  onSelect: (id: string) => void;
  selectedId: string;
  focusedId: string;
  /**
   * #2183: per-(unit/agent) open-issue counts keyed on definitionId. The
   * map covers every subject in the tree; rows whose subject isn't in
   * the map render no badge (same as zero counts).
   */
  issueCountsByDefinitionId: Map<
    string,
    { errorCount: number; warningCount: number }
  >;
  hideCountBadge?: boolean;
}

function TreeRow({
  node,
  depth,
  expanded,
  onToggle,
  onSelect,
  selectedId,
  focusedId,
  issueCountsByDefinitionId,
  hideCountBadge,
}: TreeRowProps) {
  const children = childrenOf(node);
  const hasChildren = children.length > 0;
  const isOpen = !!expanded[node.id];
  const selected = selectedId === node.id;
  const isTabStop = focusedId === node.id;
  const subtree = useMemo(() => aggregate(node), [node]);

  // Branches close-up surface their *worst* descendant status so a failing
  // agent buried four levels deep paints the row red even when collapsed.
  const dotStatus: NodeStatus =
    hasChildren && !isOpen ? subtree.worst : node.status;

  return (
    <>
      <div
        role="treeitem"
        aria-selected={selected}
        aria-level={depth + 1}
        aria-expanded={hasChildren ? isOpen : undefined}
        // Roving tabindex: exactly one row in the tree carries `0`; the
        // rest carry `-1` so Tab lands on the tree once, and arrow keys
        // take over from there.
        tabIndex={isTabStop ? 0 : -1}
        data-testid={`tree-row-${node.id}`}
        data-kind={node.kind}
        data-status={node.status}
        onClick={() => onSelect(node.id)}
        // Indentation is driven by aria-level: 14px per level, plus 8px
        // outside padding so the twisty has a comfortable hit target on
        // the very first level.
        style={{ paddingLeft: 8 + depth * 14 }}
        className={cn(
          "group flex h-7 cursor-pointer items-center gap-1.5 rounded-md pr-2 text-xs transition-colors focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring",
          selected
            ? "bg-primary/10 text-primary"
            : "text-foreground hover:bg-accent",
        )}
      >
        <button
          type="button"
          tabIndex={-1}
          onClick={(e) => {
            e.stopPropagation();
            if (hasChildren) onToggle(node.id);
          }}
          aria-hidden={!hasChildren}
          aria-label={
            hasChildren
              ? isOpen
                ? `Collapse ${node.name}`
                : `Expand ${node.name}`
              : undefined
          }
          data-testid={hasChildren ? `tree-twisty-${node.id}` : undefined}
          className={cn(
            "flex h-4 w-4 shrink-0 items-center justify-center text-muted-foreground",
            !hasChildren && "pointer-events-none invisible",
          )}
        >
          {hasChildren ? (isOpen ? "▾" : "▸") : ""}
        </button>
        <span
          aria-hidden="true"
          data-testid={`tree-status-dot-${node.id}`}
          data-status={dotStatus}
          className={cn(
            "h-1.5 w-1.5 shrink-0 rounded-full",
            statusDotClass(dotStatus),
          )}
        />
        <KindIcon
          kind={node.kind}
          className={cn(
            "h-3 w-3 shrink-0",
            selected ? "text-primary" : "text-muted-foreground",
          )}
        />
        <span className="flex-1 truncate">{node.name}</span>
        <IssueBadges
          node={node}
          issueCountsByDefinitionId={issueCountsByDefinitionId}
        />
        {hasChildren && !hideCountBadge ? (
          <span className="font-mono text-[10px] text-muted-foreground">
            {subtree.agents}
          </span>
        ) : null}
      </div>
      {hasChildren && isOpen
        ? children.map((child) => (
            <TreeRow
              key={child.id}
              node={child}
              depth={depth + 1}
              expanded={expanded}
              onToggle={onToggle}
              onSelect={onSelect}
              selectedId={selectedId}
              focusedId={focusedId}
              issueCountsByDefinitionId={issueCountsByDefinitionId}
              hideCountBadge={hideCountBadge}
            />
          ))
        : null}
    </>
  );
}

/**
 * #2183: tiny per-row chip(s) showing the row's open-issue counts.
 * Renders nothing for the Tenant root and for rows whose subject has
 * no entries in the counts map. Errors get a red chip with the count;
 * warnings get a yellow chip. Single-issue chips drop the count for
 * a tighter render.
 */
function IssueBadges({
  node,
  issueCountsByDefinitionId,
}: {
  node: TreeNode;
  issueCountsByDefinitionId: Map<
    string,
    { errorCount: number; warningCount: number }
  >;
}) {
  if (node.kind === "Tenant" || !node.definitionId) return null;
  const counts = issueCountsByDefinitionId.get(node.definitionId);
  if (!counts || (counts.errorCount === 0 && counts.warningCount === 0)) {
    return null;
  }
  const ariaParts: string[] = [];
  if (counts.errorCount > 0) {
    ariaParts.push(
      `${counts.errorCount} ${counts.errorCount === 1 ? "error" : "errors"}`,
    );
  }
  if (counts.warningCount > 0) {
    ariaParts.push(
      `${counts.warningCount} ${counts.warningCount === 1 ? "warning" : "warnings"}`,
    );
  }
  return (
    <span
      className="flex shrink-0 items-center gap-1"
      data-testid={`tree-issue-badges-${node.id}`}
      aria-label={`${ariaParts.join(", ")} on ${node.name}`}
    >
      {counts.errorCount > 0 && (
        <span
          data-testid={`tree-issue-badge-error-${node.id}`}
          className="rounded bg-destructive/15 px-1 py-0.5 text-[10px] font-medium text-destructive"
        >
          {counts.errorCount > 1 ? counts.errorCount : "!"}
        </span>
      )}
      {counts.warningCount > 0 && (
        <span
          data-testid={`tree-issue-badge-warning-${node.id}`}
          className="rounded bg-warning/15 px-1 py-0.5 text-[10px] font-medium text-foreground"
        >
          {counts.warningCount > 1 ? counts.warningCount : "!"}
        </span>
      )}
    </span>
  );
}

/**
 * Static, lint-friendly icon picker. Colocated here (not lifted to a
 * shared module) because the unit-tree and unit-detail-pane each get their
 * own copy with potentially-different sizing defaults.
 *
 * Defined at module scope so the `react-hooks/static-components` rule —
 * which forbids capital-cased component aliases inside render — sees a
 * stable component reference.
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
    case "Human":
      // #2466: humans render as tree rows under every unit they are a
      // team-role member of. Same `User` glyph the Detail Pane uses
      // for the kind-icon cluster so the tree and the page chrome
      // share one visual language.
      return <User aria-hidden="true" className={className} />;
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
