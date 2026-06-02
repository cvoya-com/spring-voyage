/**
 * Lenient-and-loud validator for the `GET /api/v1/tenant/tree` payload.
 *
 * `aggregate()` and `<UnitTree>` assume every node's `kind` and `status`
 * sit inside the declared unions. If the server ever emits a value
 * outside those sets (new lifecycle state added server-side without a
 * matching client build, malformed payload, proxy tampering) the UI
 * silently miscounts: `STATUS_RANK[unknownStatus]` returns `undefined`
 * and the worst-status rollup drops the failing node.
 *
 * This pass coerces unknown values to safe defaults (`Unit` / `error`)
 * and logs every coercion via `console.error` so the error-tracking
 * pipeline catches the drift. The UI still renders — operators see the
 * node — but the log trail points straight at the contract mismatch.
 *
 * See `FOUND-tree-boundary-validate` (umbrella #815) for background.
 */

import type {
  TenantTreeNode as WireTenantTreeNode,
  TenantTreeResponse,
} from "./types";

// `Human` was added in #2466 — the tree now emits a Human child under
// every unit the human is a team-role member of. Duplicates across units
// are intentional (a single human can be on multiple teams; OSS hosts
// one operator who appears once per unit they participate in).
const NODE_KINDS = ["Tenant", "Unit", "Agent", "Human"] as const;
// Mirrors the `NodeStatus` union in `@/components/units/aggregate`.
// `draft`, `stopping`, and `validating` were added in #1032 when the
// tenant-tree endpoint started emitting the unit's real lifecycle status
// instead of pinning every node to `"running"`. `unknown` (#3006) is the
// read-failure / degraded indicator the endpoint emits when an actor-state
// read fails or is canceled.
const NODE_STATUSES = [
  "running",
  "starting",
  "paused",
  "stopped",
  "stopping",
  "error",
  "draft",
  "validating",
  "unknown",
] as const;

type NodeKind = (typeof NODE_KINDS)[number];
type NodeStatus = (typeof NODE_STATUSES)[number];

function isNodeKind(v: unknown): v is NodeKind {
  return typeof v === "string" && (NODE_KINDS as readonly string[]).includes(v);
}

function isNodeStatus(v: unknown): v is NodeStatus {
  return (
    typeof v === "string" && (NODE_STATUSES as readonly string[]).includes(v)
  );
}

/**
 * The openapi-typescript generator widens every numeric schema field to
 * `number | string | null` because Kiota treats `int64`/`double` as
 * potentially-lossy when crossing a JSON boundary. Normalize to a plain
 * `number` (or `undefined`) so the Explorer's `aggregate()` math never
 * receives a string operand.
 */
function toNumber(v: unknown): number | undefined {
  if (typeof v === "number" && Number.isFinite(v)) return v;
  if (typeof v === "string" && v.trim() !== "") {
    const n = Number(v);
    return Number.isFinite(n) ? n : undefined;
  }
  return undefined;
}

/**
 * Validated tenant-tree node — the shape `<UnitExplorer>` consumes.
 * Identical wire shape, narrowed enum fields, and `children` guaranteed
 * to be either absent or an array of already-validated children.
 */
export interface ValidatedTenantTreeNode {
  id: string;
  name: string;
  kind: NodeKind;
  status: NodeStatus;
  desc?: string;
  cost24h?: number;
  msgs24h?: number;
  role?: string;
  skills?: number;
  primaryParentId?: string;
  /**
   * #2183: stable definition Guid for unit / agent nodes. Carried so
   * the tree-explorer issue-counts batch query can address rows without
   * re-resolving the navigation slug. Absent for the synthesized Tenant
   * root.
   */
  definitionId?: string;
  children?: ValidatedTenantTreeNode[];
}

/**
 * Coerce a wire `TenantTreeNode` to the validated shape. Unknown `kind`
 * collapses to `Unit` (the kind with the broadest valid fields so a
 * mystery node still passes through the tree renderer). Unknown
 * `status` collapses to `error` (makes the drift visible — an operator
 * sees the node painted red rather than the mystery rendering as
 * "running"). Every coercion logs.
 */
function coerceNode(
  wire: WireTenantTreeNode,
  path: string,
): ValidatedTenantTreeNode {
  let kind: NodeKind;
  if (isNodeKind(wire.kind)) {
    kind = wire.kind;
  } else {
    console.error(
      "tenant-tree: unexpected kind; coerced to Unit",
      { path, kind: wire.kind },
    );
    kind = "Unit";
  }

  let status: NodeStatus;
  if (isNodeStatus(wire.status)) {
    status = wire.status;
  } else {
    console.error(
      "tenant-tree: unexpected status; coerced to error",
      { path, status: wire.status },
    );
    status = "error";
  }

  const children = wire.children
    ? wire.children.map((c, i) => coerceNode(c, `${path}/${i}`))
    : undefined;

  return {
    id: wire.id,
    name: wire.name,
    kind,
    status,
    desc: wire.desc ?? undefined,
    cost24h: toNumber(wire.cost24h),
    msgs24h: toNumber(wire.msgs24h),
    role: wire.role ?? undefined,
    skills: toNumber(wire.skills),
    primaryParentId: wire.primaryParentId ?? undefined,
    definitionId: wire.definitionId ?? undefined,
    children,
  };
}

/** Coerce the full response. Top-level node is always addressed as `$`. */
export function validateTenantTreeResponse(
  response: TenantTreeResponse,
): ValidatedTenantTreeNode {
  return coerceNode(response.tree, "$");
}
