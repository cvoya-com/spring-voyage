"use client";

import { Bot, Globe, Layers } from "lucide-react";
import { useCallback, useMemo, useState } from "react";

import { cn } from "@/lib/utils";

import {
  aggregate,
  childrenOf,
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
 * Keyboard navigation (arrow keys, Home/End, type-ahead) is intentionally
 * NOT wired in this PR. Per the foundation plan, those handlers ship in
 * `V21-tree-keyboard`; the static ARIA roles here let screen readers pick
 * up the structure today and form the contract the v2.1 keyboard work
 * will fulfil.
 */
export function UnitTree({
  tree,
  selectedId,
  onSelect,
  defaultExpanded,
  className,
}: UnitTreeProps) {
  const [expanded, setExpanded] = useState<Record<string, boolean>>(
    () => defaultExpanded ?? { [tree.id]: true },
  );
  const toggle = useCallback(
    (id: string) =>
      setExpanded((prev) => ({ ...prev, [id]: !prev[id] })),
    [],
  );

  return (
    <div
      role="tree"
      aria-label="Units & agents"
      data-testid="unit-tree"
      className={cn("flex flex-col gap-px py-1", className)}
    >
      <TreeRow
        node={tree}
        depth={0}
        expanded={expanded}
        onToggle={toggle}
        onSelect={onSelect}
        selectedId={selectedId}
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
}

function TreeRow({
  node,
  depth,
  expanded,
  onToggle,
  onSelect,
  selectedId,
}: TreeRowProps) {
  const children = childrenOf(node);
  const hasChildren = children.length > 0;
  const isOpen = !!expanded[node.id];
  const selected = selectedId === node.id;
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
        data-testid={`tree-row-${node.id}`}
        data-kind={node.kind}
        data-status={node.status}
        onClick={() => onSelect(node.id)}
        // Indentation is driven by aria-level — the inline style mirrors
        // the design kit's 14px-per-level rule. Padded with 8px on the
        // outside so the twisty has a comfortable hit target on the very
        // first level.
        style={{ paddingLeft: 8 + depth * 14 }}
        className={cn(
          "group flex h-7 cursor-pointer items-center gap-1.5 rounded-md pr-2 text-xs transition-colors",
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
        {hasChildren ? (
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
            />
          ))
        : null}
    </>
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
