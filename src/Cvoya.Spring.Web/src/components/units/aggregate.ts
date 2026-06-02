export type NodeKind = "Tenant" | "Unit" | "Agent" | "Human";

export type NodeStatus =
  | "running"
  | "starting"
  | "paused"
  | "stopped"
  | "stopping"
  | "error"
  | "draft"
  | "validating"
  // #3006: read-time degraded indicator — the server emits this when a unit's
  // actor-state read failed or was canceled (rather than reporting a misleading
  // "draft"). Mirrored in `validate-tenant-tree.ts`'s `NODE_STATUSES`.
  | "unknown";

interface BaseNode {
  /** Stable identifier — used as React `key`, URL `?node=`, and index key. */
  id: string;
  /** Human-readable name shown in the tree row + detail pane title. */
  name: string;
  status: NodeStatus;
  /** Optional one-line description rendered above the Overview stat tiles. */
  desc?: string;
  /**
   * #2183: stable definition Guid. Carried for unit / agent nodes so the
   * tree-explorer badge query can address the row without re-resolving
   * the navigation slug. Null for the synthesized Tenant root.
   */
  definitionId?: string | null;
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

/**
 * Human subject node (#2266 / #2466). Humans are first-class participants
 * in threads (per [`docs/concepts/humans.md`](../../../../docs/concepts/humans.md))
 * but they are **not** agents — they do not carry expertise, runtime,
 * memory, skills, traces, or budget. The node interface mirrors only
 * the cross-subject identity fields (`id`, `name`, `status`, `desc`).
 *
 * As of #2466, `GET /api/v1/tenant/tree` emits a Human child under every
 * unit the human is a team-role member of. The node id carries the
 * canonical `human://<guid>` scheme prefix; clicking the row dispatches
 * through the Explorer's existing `human:` selection handler so the
 * dedicated `/humans/<id>` route is the target — the unit tree shows
 * "who is on this team?" but the Human detail surface owns the
 * per-person rendering.
 *
 * Multiple instances of the same human (e.g. the OSS operator listed on
 * several units) are intentional — each `(unit, human)` membership pair
 * is its own tree row.
 *
 * `status` is always `"running"` for humans — the field is required by
 * `BaseNode` for status-rank rollups, but a human has no lifecycle. The
 * Detail Pane's status badge suppresses the lifecycle chrome for human
 * subjects (see `unit-detail-pane.tsx`).
 *
 * `email` and `platformRole` are surfaced on the Human × Overview tab
 * body itself via the live `useHuman(id)` query — the tree-side wire
 * shape never carries them, so the node interface stays minimal.
 */
export interface HumanNode extends BaseNode {
  kind: "Human";
}

export type TreeNode = TenantNode | UnitNode | AgentNode | HumanNode;

/**
 * Returns the node's children, or an empty readonly array for kinds that
 * can't have children (Agent, Human). Lets callers iterate without
 * re-narrowing.
 */
export function childrenOf(node: TreeNode): readonly TreeNode[] {
  if (node.kind === "Agent" || node.kind === "Human") return [];
  return node.children ?? [];
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
  error: 8,
  // `unknown` (#3006) is a read-failure / degraded indicator — rank it just
  // below `error` so a unit whose status couldn't be read surfaces on a
  // collapsed branch ahead of transitional states, but never outranks a real
  // error.
  unknown: 7,
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
  // Humans and tenants carry no self cost/messages — humans don't have
  // a runtime (they're addressable subjects, not agents; see
  // `docs/concepts/humans.md`), tenants roll up purely through their
  // children. Agent and Unit carry per-row 24h costs / message volumes.
  const carriesCost = node.kind === "Unit" || node.kind === "Agent";
  let cost = carriesCost ? node.cost24h ?? 0 : 0;
  let msgs = carriesCost ? node.msgs24h ?? 0 : 0;
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
    // branch: agents and humans have no children, so `filteredChildren`
    // is empty for them and we exited above. The narrowing dance keeps
    // TS happy about the discriminated-union shape — a plain
    // `{ ...node, children: filteredChildren }` is rejected because the
    // compiler doesn't fold "agents / humans have no children" through
    // the early-return.
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
  // A unit is an agent (see docs/concepts/units-vs-agents.md), so it
  // gains the agent's introspection slots (Skills, Traces) and the
  // lifecycle slot (Deployment). Clones is the only agent-only slot —
  // units cannot be cloned today. See canonical-tabs.md § 7.1.
  //
  // `Members` replaces the v0.1-historical `Agents` slot (#2270 /
  // #2427) — the Unit composition tab now surfaces all three subject
  // kinds that participate in the unit: agent members, sub-units, and
  // human team-role members. The rename is hard (no `?tab=Agents`
  // shim, per v0.1 aggressive-cleanup convention); the DetailPane's
  // existing invalid-tab fallback transparently bounces stale links
  // back to Overview.
  visible: [
    "Overview",
    "Activity",
    "Messages",
    "Memory",
    "Members",
    "Skills",
    "Traces",
    "Policies",
  ] as const,
  // Deployment is in overflow on both Unit and Agent — both are deep
  // editors / lifecycle surfaces. The activity-side cluster
  // (Overview/Activity/Messages/Memory + composition slots) is the
  // high-frequency surface; overflow placement preserves the existing
  // `?tab=Deployment` deep-link contract.
  overflow: ["Config", "Deployment"] as const,
};

export const AGENT_TABS = {
  // Config + Deployment are overflow per canonical-tabs.md § 3.3 / § 7.1:
  // both are deep editors / lifecycle surfaces; the activity-side cluster
  // (Overview / Activity / Messages / Memory / Skills / Traces / Clones /
  // Policies) is the high-frequency surface. #1119's Deployment tab is the
  // full-fidelity persistent-agent lifecycle surface; the AgentCard
  // Deployment quick-action deep-links to `?tab=Deployment` so overflow
  // placement does not regress that path. Mirrors UNIT_TABS shape.
  visible: [
    "Overview",
    "Activity",
    "Messages",
    "Memory",
    "Skills",
    "Traces",
    "Clones",
    "Policies",
  ] as const,
  overflow: ["Config", "Deployment"] as const,
};

export const HUMAN_TABS = {
  // Human is a fourth subject per `docs/concepts/humans.md` and ADR-0044.
  // Humans implement only `IMessageReceiver` — they participate in
  // threads but do not carry expertise, runtime execution, memory,
  // skills, traces, clones, budgets, or policies. The catalog mirrors
  // the v0.2 design in `docs/design/canonical-tabs.md` § 3.3 / § 4:
  // Overview + Messages visible, Config in overflow.
  //
  // #2266 lands the catalog; #2267 lands the Overview body. Messages
  // (#2268) and Config (#2269) follow in Portal Wave B — until then
  // the registry's `<TabPlaceholder>` renders the "coming soon" copy
  // for the deferred slots.
  visible: [
    "Overview",
    "Messages",
  ] as const,
  overflow: ["Config"] as const,
};

export const TENANT_TABS = {
  // Memory, Messages, Agents, Skills, Traces, Clones, and Deployment
  // are intentionally absent — Tenant does not participate in threads,
  // does not compose thread participants, does not have memory, and is
  // not addressable as an agent. See canonical-tabs.md § 1 principle
  // and § 4.1.
  visible: [
    "Overview",
    "Activity",
    "Policies",
    "Budgets",
  ] as const,
  // Config holds the three settings reached today via `/settings`:
  // tenant-default credentials, tenant budget editor, and tenant
  // cloning-policy read-only summary. `/settings` continues to embed
  // the same panel bodies — one canonical implementation, two access
  // paths. Overflow because Config is a deep editor reached less often
  // than Overview / Activity / Policies / Budgets, mirroring the
  // Unit and Agent split. See canonical-tabs.md § 5.11.
  overflow: ["Config"] as const,
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
export type HumanTabName =
  | (typeof HUMAN_TABS.visible)[number]
  | (typeof HUMAN_TABS.overflow)[number];
export type TabName = UnitTabName | AgentTabName | TenantTabName | HumanTabName;

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
      : K extends "Human"
        ? HumanTabName
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
    case "Human":
      return HUMAN_TABS as unknown as {
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
