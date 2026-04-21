export type NodeKind = "Tenant" | "Unit" | "Agent";

export type NodeStatus =
  | "running"
  | "starting"
  | "paused"
  | "stopped"
  | "error";

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

const STATUS_RANK: Record<NodeStatus, number> = {
  error: 4,
  starting: 3,
  paused: 2,
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
 * Tab catalogs by node kind. The order here is the order the tab strip
 * renders.
 */
export const UNIT_TABS = [
  "Overview",
  "Agents",
  "Orchestration",
  "Activity",
  "Messages",
  "Memory",
  "Policies",
  "Config",
] as const;

export const AGENT_TABS = [
  "Overview",
  "Activity",
  "Messages",
  "Memory",
  "Skills",
  "Traces",
  "Clones",
  "Config",
] as const;

export const TENANT_TABS = [
  "Overview",
  "Activity",
  "Policies",
  "Budgets",
  "Memory",
] as const;

export type UnitTabName = (typeof UNIT_TABS)[number];
export type AgentTabName = (typeof AGENT_TABS)[number];
export type TenantTabName = (typeof TENANT_TABS)[number];
export type TabName = UnitTabName | AgentTabName | TenantTabName;

/**
 * Conditional type linking a node kind to its tab catalog. Lets generic
 * registry APIs (`registerTab`, `lookupTab`, `tabKey`) reject nonsense
 * `(kind, tab)` pairs like `("Tenant", "Skills")` at compile time.
 */
export type TabsFor<K extends NodeKind> = K extends "Tenant"
  ? TenantTabName
  : K extends "Unit"
    ? UnitTabName
    : K extends "Agent"
      ? AgentTabName
      : never;

export function tabsFor<K extends NodeKind>(kind: K): readonly TabsFor<K>[] {
  switch (kind) {
    case "Agent":
      return AGENT_TABS as unknown as readonly TabsFor<K>[];
    case "Tenant":
      return TENANT_TABS as unknown as readonly TabsFor<K>[];
    case "Unit":
    default:
      return UNIT_TABS as unknown as readonly TabsFor<K>[];
  }
}
