export type NodeKind = "Tenant" | "Unit" | "Agent";

export type NodeStatus =
  | "running"
  | "starting"
  | "paused"
  | "stopped"
  | "stopping"
  | "error"
  | "draft"
  | "validating";

interface BaseNode {
  /** Stable identifier — used as React `key`, URL `?node=`, and index key. */
  id: string;
  /** Human-readable name shown in the tree row + detail pane title. */
  name: string;
  status: NodeStatus;
  /** Optional one-line description rendered above the Overview stat tiles. */
  desc?: string;
}

export interface TenantNode extends BaseNode {
  kind: "Tenant";
  /**
   * Top-level units under the tenant. Tenant carries no self cost/msgs —
   * those are derived by walking children via {@link aggregate}.
   */
  children?: TreeNode[];
}

export interface UnitNode extends BaseNode {
  kind: "Unit";
  /** Self cost in USD over the last 24 h. Subtree totals via {@link aggregate}. */
  cost24h?: number;
  /** Self message volume over the last 24 h. Subtree totals via {@link aggregate}. */
  msgs24h?: number;
  /** Direct children — nested units and/or agents. */
  children?: TreeNode[];
}

export interface AgentNode extends BaseNode {
  kind: "Agent";
  /** Agent role (e.g. "tech-lead", "reviewer"). */
  role?: string;
  /** Number of skills equipped — drives the Skills count tile. */
  skills?: number;
  /** Self cost in USD over the last 24 h. */
  cost24h?: number;
  /** Self message volume over the last 24 h. */
  msgs24h?: number;
  /**
   * For multi-parent agents: the id of the parent that owns the canonical
   * surface. Aliases (agent under a non-primary parent) render deduplicated.
   */
  primaryParentId?: string;
}

export type TreeNode = TenantNode | UnitNode | AgentNode;

/**
 * Returns the node's children, or an empty readonly array for kinds that
 * can't have children (Agent). Lets callers iterate without re-narrowing.
 */
export function childrenOf(node: TreeNode): readonly TreeNode[] {
  return node.kind === "Agent" ? [] : node.children ?? [];
}

/**
 * Subtree roll-up returned by {@link aggregate}.
 *
 * The roll-up includes the node it was called on. Both `units` and `agents`
 * count *every* unit/agent in the subtree, so the root node call returns
 * a tenant-wide total and a leaf-agent call returns `{ agents: 1, units: 0 }`.
 *
 * `worst` ranks statuses by severity (`error > starting > paused > running > stopped`)
 * and walks the subtree to surface the most concerning state. Used by
 * tree rows to colour the status dot of a *collapsed* branch — operators
 * can spot a failing agent buried four levels deep without expanding.
 */
export interface SubtreeAggregate {
  cost: number;
  msgs: number;
  agents: number;
  units: number;
  worst: NodeStatus;
}

// Severity ordering for the worst-status roll-up painted on collapsed
// tree rows. `draft` is unconfigured (operator hasn't finished setup)
// and `validating` / `stopping` are transitional — rank them between
// `stopped` and `starting` so a subtree containing a Draft unit paints
// stronger than a plain stopped subtree but doesn't outrank a node
// that's actively transitioning / erroring.
const STATUS_RANK: Record<NodeStatus, number> = {
  error: 7,
  starting: 6,
  stopping: 5,
  validating: 4,
  paused: 3,
  draft: 2,
  running: 1,
  stopped: 0,
};

/**
 * Recursively roll up cost, message volume, agent count, unit count, and
 * the worst-status-in-subtree for a node. Pure function — given the same
 * tree it returns the same result, so memoise around it freely.
 */
export function aggregate(node: TreeNode): SubtreeAggregate {
  let cost = node.kind === "Tenant" ? 0 : node.cost24h ?? 0;
  let msgs = node.kind === "Tenant" ? 0 : node.msgs24h ?? 0;
  let agents = node.kind === "Agent" ? 1 : 0;
  let units = node.kind === "Unit" ? 1 : 0;
  let worst: NodeStatus = node.status;

  for (const child of childrenOf(node)) {
    const sub = aggregate(child);
    cost += sub.cost;
    msgs += sub.msgs;
    agents += sub.agents;
    units += sub.units;
    if (STATUS_RANK[sub.worst] > STATUS_RANK[worst]) {
      worst = sub.worst;
    }
  }

  return { cost, msgs, agents, units, worst };
}

/**
 * Flatten a tree into a depth-first list of `{ node, path }` records.
 * `path` is the chain of ancestors from the root down to and including `node`.
 */
export function flattenTree(
  node: TreeNode,
  path: TreeNode[] = [],
  out: Array<{ node: TreeNode; path: TreeNode[] }> = [],
): Array<{ node: TreeNode; path: TreeNode[] }> {
  const here = [...path, node];
  out.push({ node, path: here });
  for (const child of childrenOf(node)) {
    flattenTree(child, here, out);
  }
  return out;
}

/**
 * Build an `id → { node, path }` index for fast selection lookup.
 */
export function findIndex(tree: TreeNode): {
  byId: Record<string, { node: TreeNode; path: TreeNode[] }>;
} {
  const all = flattenTree(tree);
  const byId: Record<string, { node: TreeNode; path: TreeNode[] }> = {};
  for (const entry of all) {
    byId[entry.node.id] = entry;
  }
  return { byId };
}

/**
 * Substring filter over a node tree. Returns a *new* tree containing only
 * matching nodes, the ancestors that lead to them, **and** every
 * descendant of any node whose own name matched, plus the set of
 * matching node ids so callers can paint hits or auto-expand branches.
 *
 * Filter shape: **case-insensitive substring** on `name`. Picked over fuzzy
 * matching for v0.1 — predictable, dependency-free, easy to upgrade later
 * (a fuzzy library can replace `nodeMatches` without changing this
 * function's shape). Matches both units *and* agents (the search input is
 * placeholdered "Search units & agents…").
 *
 * Three pruning rules combine to give operators a navigable mid-search
 * tree:
 *
 * 1. A node whose own name matches is kept along with **its entire
 *    subtree** — searching "engineering" surfaces every agent / sub-unit
 *    inside Engineering so operators can drill into the branch the hit
 *    pointed them at.
 * 2. A node whose own name does *not* match is kept iff at least one
 *    descendant matches — matching ancestors stay visible so the path
 *    to every hit is intact.
 * 3. A node with neither a self-match nor a matching descendant is
 *    dropped.
 *
 * Empty / whitespace-only `query` is a no-op — the original tree is
 * returned and `matches` is empty so callers can short-circuit.
 *
 * Pure function — call sites should memoise on `(tree, query)`.
 */
export interface FilterResult {
  /**
   * Pruned tree, or `null` when nothing in the subtree (including the
   * node itself) matches. The top-level call returns `null` to mean
   * "no nodes match the query" so the caller can render an empty-state.
   */
  tree: TreeNode | null;
  /** Ids of every node whose own `name` matched the query. */
  matches: Set<string>;
}

export function filterTree(tree: TreeNode, query: string): FilterResult {
  const trimmed = query.trim();
  if (trimmed.length === 0) {
    return { tree, matches: new Set() };
  }
  const needle = trimmed.toLowerCase();
  const matches = new Set<string>();

  function nodeMatches(node: TreeNode): boolean {
    return node.name.toLowerCase().includes(needle);
  }

  function walk(node: TreeNode): TreeNode | null {
    const selfMatches = nodeMatches(node);
    if (selfMatches) matches.add(node.id);

    // When the node itself matches, its whole subtree stays — operators
    // expect "search engineering" to surface every agent / sub-unit
    // inside Engineering, not just the row labelled "Engineering". Still
    // walk descendants so their match ids land in `matches`.
    if (selfMatches) {
      for (const child of childrenOf(node)) walk(child);
      return node;
    }

    const filteredChildren: TreeNode[] = [];
    for (const child of childrenOf(node)) {
      const kept = walk(child);
      if (kept) filteredChildren.push(kept);
    }

    if (filteredChildren.length === 0) {
      return null;
    }

    // Reattach the filtered children. Only Tenant / Unit reach this
    // branch: agents have no children, so `filteredChildren` is empty
    // for them and we exited above. The narrowing dance keeps TS happy
    // about the discriminated-union shape — a plain `{ ...node,
    // children: filteredChildren }` is rejected because the compiler
    // doesn't fold "agents have no children" through the early-return.
    if (node.kind === "Tenant") {
      return { ...node, children: filteredChildren };
    }
    if (node.kind === "Unit") {
      return { ...node, children: filteredChildren };
    }
    return node;
  }

  return { tree: walk(tree), matches };
}

/**
 * Tab catalogs by node kind. Each catalog is split into `visible` (the
 * primary tab strip) and `overflow` (tabs that render through a secondary
 * affordance — e.g. a trailing separator + strip, or a "more" popover).
 *
 * The split is structural on purpose: consumers can read the contract
 * ("Config is the Unit overflow tab") from the type, without parsing
 * labels or re-reading the plan. `tabsFor` still returns the flat
 * concatenation so any consumer that only cares about "every tab this
 * kind supports" keeps working unchanged.
 *
 * The order inside each bucket is the order the respective strip
 * renders. Overflow tabs follow visible ones.
 */
export const UNIT_TABS = {
  visible: [
    "Overview",
    "Agents",
    "Orchestration",
    "Activity",
    "Messages",
    "Memory",
    "Policies",
  ] as const,
  overflow: ["Config"] as const,
};

export const AGENT_TABS = {
  visible: [
    "Overview",
    "Activity",
    "Messages",
    "Memory",
    "Skills",
    "Traces",
    "Clones",
    "Policies",
    "Config",
    // #1119: dedicated Deployment tab for the persistent-agent lifecycle
    // verbs (deploy / undeploy / scale / status / logs). The Overview tab
    // already surfaces a compact lifecycle panel; this tab is the
    // full-fidelity surface that matches `spring agent {deploy,undeploy,
    // scale,logs}` 1:1 and is always reachable via deep-link.
    "Deployment",
  ] as const,
  overflow: [] as const,
};

export const TENANT_TABS = {
  visible: [
    "Overview",
    "Activity",
    "Policies",
    "Budgets",
    "Memory",
  ] as const,
  overflow: [] as const,
};

export type UnitTabName =
  | (typeof UNIT_TABS.visible)[number]
  | (typeof UNIT_TABS.overflow)[number];
export type AgentTabName =
  | (typeof AGENT_TABS.visible)[number]
  | (typeof AGENT_TABS.overflow)[number];
export type TenantTabName =
  | (typeof TENANT_TABS.visible)[number]
  | (typeof TENANT_TABS.overflow)[number];
export type TabName = UnitTabName | AgentTabName | TenantTabName;

/**
 * Conditional type linking a node kind to its tab catalog. Lets generic
 * registry APIs (`registerTab`, `lookupTab`, `tabKey`) reject nonsense
 * `(kind, tab)` pairs like `("Tenant", "Skills")` at compile time.
 *
 * The type covers *both* visible and overflow tabs — overflow tabs stay
 * first-class citizens of the registry; the visible/overflow split only
 * affects how the Detail Pane surfaces them, not the runtime dispatch.
 */
export type TabsFor<K extends NodeKind> = K extends "Tenant"
  ? TenantTabName
  : K extends "Unit"
    ? UnitTabName
    : K extends "Agent"
      ? AgentTabName
      : never;

function catalogFor<K extends NodeKind>(
  kind: K,
): { visible: readonly TabsFor<K>[]; overflow: readonly TabsFor<K>[] } {
  switch (kind) {
    case "Agent":
      return AGENT_TABS as unknown as {
        visible: readonly TabsFor<K>[];
        overflow: readonly TabsFor<K>[];
      };
    case "Tenant":
      return TENANT_TABS as unknown as {
        visible: readonly TabsFor<K>[];
        overflow: readonly TabsFor<K>[];
      };
    case "Unit":
    default:
      return UNIT_TABS as unknown as {
        visible: readonly TabsFor<K>[];
        overflow: readonly TabsFor<K>[];
      };
  }
}

/**
 * Flat catalog of every tab a kind supports — visible tabs first, then
 * overflow tabs. This is the union used by consumers that only need
 * "does this kind have this tab?" semantics (URL ⇄ tab validation, the
 * `register-all` completeness test, etc.).
 */
export function tabsFor<K extends NodeKind>(kind: K): readonly TabsFor<K>[] {
  const c = catalogFor(kind);
  return [...c.visible, ...c.overflow];
}

/** Visible tabs for the kind — the primary tab strip. */
export function visibleTabsFor<K extends NodeKind>(
  kind: K,
): readonly TabsFor<K>[] {
  return catalogFor(kind).visible;
}

/**
 * Overflow tabs for the kind — rendered via a secondary affordance (the
 * Detail Pane currently uses a trailing, visually-separated `<TabStrip>`).
 * Kinds with no overflow return an empty readonly array.
 */
export function overflowTabsFor<K extends NodeKind>(
  kind: K,
): readonly TabsFor<K>[] {
  return catalogFor(kind).overflow;
}
