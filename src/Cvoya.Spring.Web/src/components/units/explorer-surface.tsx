"use client";

// Shared Explorer canvas (#2473). `/units`, `/explorer`, and
// `/explorer/units/[id]` all render this surface; the route file only
// contributes the entry-point redirect logic. Selection is read from
// the URL snapshot:
//   - canonical units/agents: `/explorer/units/<id>` → selectedId from path
//   - canonical humans: `/explorer/humans/<id>` → `human:<id>` from path
//   - legacy: `?node=<id>` is rewritten by the entry-point redirect; the
//     canvas itself never reads it.
//
// All node/tab writes go through `window.history.replaceState` + the
// Explorer URL-change event so the canvas stays mounted across clicks
// (App Router navigations would tear down the tree on every selection).
// Human nodes write to `/explorer/humans/<guid>` (#2517) so the tree
// stays visible and the URL follows the consistent `/explorer/*` pattern.

import {
  useCallback,
  useMemo,
  useSyncExternalStore,
} from "react";
import Link from "next/link";
import { Loader2, Plus } from "lucide-react";

import { ApiErrorMessage } from "@/components/ui/api-error-message";
import { Card, CardContent } from "@/components/ui/card";
import { UnitExplorer } from "@/components/units/unit-explorer";
import type { TabName, TreeNode } from "@/components/units/aggregate";
import { useTenantTree } from "@/lib/api/queries";
import type { ValidatedTenantTreeNode } from "@/lib/api/validate-tenant-tree";
import {
  dispatchExplorerUrlChange,
  getExplorerPathnameSnapshot,
  getExplorerSearchSnapshot,
  getServerExplorerPathnameSnapshot,
  getServerExplorerSearchSnapshot,
  subscribeExplorerUrl,
  toExplorerPathSegment,
} from "@/lib/explorer-url";

// Side-effect import — each tab module registers itself at module
// top-level (see `src/components/units/tabs/register-all.ts`). Keeping
// the import inside the Explorer surface means tab bundles stay lazy
// until the user browses to the Explorer.
import "@/components/units/tabs/register-all";

const EXPLORER_UNITS_PREFIX = "/explorer/units/";
const EXPLORER_HUMANS_PREFIX = "/explorer/humans/";

/**
 * Pull the selected node id from the current pathname.
 *
 * - `/explorer/units/<id>`  → returns `<id>` (unit/agent/tenant address)
 * - `/explorer/humans/<id>` → returns `human://<no-dash-guid>` so the
 *   human node is looked up in the tree by its scheme-prefixed address
 *   (#2517). The UUID is normalised to no-dash form here because the
 *   server emits human node ids as `human://<no-dash-guid>` (using
 *   `GuidFormatter.Format`), while the URL may carry either form; the
 *   `byId` index key must match the server-emitted form exactly (#2531).
 */
function selectedIdFromPathname(pathname: string): string | undefined {
  if (pathname.startsWith(EXPLORER_UNITS_PREFIX)) {
    const tail = pathname.slice(EXPLORER_UNITS_PREFIX.length);
    if (!tail) return undefined;
    const slash = tail.indexOf("/");
    const id = slash === -1 ? tail : tail.slice(0, slash);
    return id ? decodeURIComponent(id) : undefined;
  }
  if (pathname.startsWith(EXPLORER_HUMANS_PREFIX)) {
    const tail = pathname.slice(EXPLORER_HUMANS_PREFIX.length);
    if (!tail) return undefined;
    const slash = tail.indexOf("/");
    const raw = slash === -1 ? tail : tail.slice(0, slash);
    if (!raw) return undefined;
    // Re-hydrate the `human://<guid>` address so the tree index can resolve
    // it against the canonical node id emitted by the server (#2517).
    // Normalise to lowercase no-dash so the key matches the `byId` entry
    // regardless of whether the URL carries the dashed / no-dash or
    // upper- / lower-case UUID form — the server emits lowercase no-dash
    // hex via `GuidFormatter.Format` (#2531).
    const decoded = decodeURIComponent(raw);
    const nodash = decoded.replace(/-/g, "");
    const normalized =
      /^[0-9a-f]{32}$/i.test(nodash) ? nodash.toLowerCase() : decoded;
    return `human://${normalized}`;
  }
  return undefined;
}

export function ExplorerSurface() {
  const pathname = useSyncExternalStore(
    subscribeExplorerUrl,
    getExplorerPathnameSnapshot,
    getServerExplorerPathnameSnapshot,
  );
  const search = useSyncExternalStore(
    subscribeExplorerUrl,
    getExplorerSearchSnapshot,
    getServerExplorerSearchSnapshot,
  );

  const searchParams = useMemo(() => new URLSearchParams(search), [search]);
  const selectedId = selectedIdFromPathname(pathname);
  const tab = (searchParams.get("tab") as TabName | null) ?? undefined;

  const treeQuery = useTenantTree();

  const writeUrl = useCallback(
    (next: { node?: string; tab?: TabName }) => {
      // Node selection lives in the path segment; tab is view state in
      // `?tab=`. Carry the existing tab forward unless the caller is
      // explicitly switching it (and clear it on a node-only switch so
      // a stale tab from a different node kind doesn't ride along —
      // #1704).
      const rawNode =
        next.node !== undefined ? next.node : selectedId ?? "";

      const params = new URLSearchParams();
      if (next.tab !== undefined) {
        params.set("tab", next.tab);
      } else if (next.node === undefined && tab) {
        // Tab unchanged, node unchanged — preserve tab.
        params.set("tab", tab);
      }
      const qs = params.toString();

      // #2517: human nodes use the `/explorer/humans/<guid>` prefix so
      // the Explorer tree stays visible and the URL follows the
      // consistent `/explorer/*` pattern. Units and agents use the
      // existing `/explorer/units/<id>` prefix.
      const humanGuid = rawNode ? parseHumanSelection(rawNode) : null;
      let target: string;
      if (humanGuid !== null) {
        target = `${EXPLORER_HUMANS_PREFIX}${encodeURIComponent(humanGuid)}${qs ? `?${qs}` : ""}`;
      } else {
        const targetNode = rawNode ? toExplorerPathSegment(rawNode) : "";
        target = targetNode
          ? `${EXPLORER_UNITS_PREFIX}${encodeURIComponent(targetNode)}${qs ? `?${qs}` : ""}`
          : `${EXPLORER_UNITS_PREFIX}${qs ? `?${qs}` : ""}`;
      }
      window.history.replaceState(null, "", target);
      dispatchExplorerUrlChange();
    },
    [selectedId, tab],
  );

  const handleSelectNode = useCallback(
    (id: string) => writeUrl({ node: id }),
    [writeUrl],
  );
  const handleTabChange = useCallback(
    (id: string, nextTab: TabName) => writeUrl({ node: id, tab: nextTab }),
    [writeUrl],
  );

  if (treeQuery.isError) {
    return (
      <Card data-testid="unit-explorer-error">
        <CardContent className="p-4">
          <ApiErrorMessage error={treeQuery.error} />
        </CardContent>
      </Card>
    );
  }

  if (treeQuery.isLoading || !treeQuery.data) {
    return (
      <div
        role="status"
        aria-live="polite"
        data-testid="unit-explorer-loading"
        className="flex h-full min-h-[50vh] items-center justify-center text-sm text-muted-foreground"
      >
        <Loader2 className="mr-2 h-4 w-4 animate-spin" aria-hidden="true" />
        Loading tenant tree…
      </div>
    );
  }

  const tree = adaptValidatedNode(treeQuery.data);

  return (
    <div
      data-testid="unit-explorer-route"
      // The page header below the layout chrome consumes ~2.5rem; subtract
      // it from the viewport-anchored height so the explorer keeps its
      // full-bleed feel without scrolling the outer surface.
      className="flex h-[calc(100vh-6rem)] min-h-[480px] flex-col gap-3"
    >
      <ExplorerPageHeader />
      <div className="min-h-0 flex-1">
        <UnitExplorer
          tree={tree}
          selectedId={selectedId}
          onSelectNode={handleSelectNode}
          tab={tab}
          onTabChange={handleTabChange}
        />
      </div>
    </div>
  );
}

function ExplorerPageHeader() {
  return (
    <header
      data-testid="units-page-header"
      className="flex shrink-0 items-center justify-end gap-2"
    >
      <Link
        href="/units/create"
        className="inline-flex h-8 items-center justify-center rounded-md bg-primary px-3 text-sm font-medium text-primary-foreground transition-colors hover:bg-primary/90 focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring focus-visible:ring-offset-2"
        data-testid="units-page-new-unit"
      >
        <Plus className="mr-1.5 h-3.5 w-3.5" aria-hidden="true" />
        New unit
      </Link>
    </header>
  );
}

function adaptValidatedNode(node: ValidatedTenantTreeNode): TreeNode {
  return node as unknown as TreeNode;
}

/**
 * #2266: parse the Guid out of an Explorer `human:<guid>` selection.
 * Accepts both `human:<guid>` and `human://<guid>`. Returns the Guid
 * in no-dash 32-char hex form (CONVENTIONS wire format for URL path
 * segments) when matched, `null` otherwise so the caller can fall
 * through to unit-tree resolution.
 *
 * Emitting no-dash keeps the `/explorer/humans/<guid>` URL segment
 * consistent with the no-dash convention used for unit/agent ids and
 * ensures round-trips through `selectedIdFromPathname` produce a key
 * that matches the server-emitted `human://<no-dash-guid>` node id
 * in the `byId` index (#2531).
 */
function parseHumanSelection(raw: string): string | null {
  const candidate = raw.startsWith("human://")
    ? raw.slice("human://".length)
    : raw.startsWith("human:")
      ? raw.slice("human:".length)
      : null;
  if (candidate === null) return null;
  const UUID_RE =
    /^[0-9a-f]{8}-?[0-9a-f]{4}-?[0-9a-f]{4}-?[0-9a-f]{4}-?[0-9a-f]{12}$/i;
  if (!UUID_RE.test(candidate)) return null;
  // Always emit no-dash form for the URL path segment (CONVENTIONS §2
  // "wire form on URLs"). Strip any dashes so both dashed and no-dash
  // inputs produce the canonical 32-char hex output.
  return candidate.replace(/-/g, "");
}
