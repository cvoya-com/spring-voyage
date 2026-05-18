"use client";

// Shared Explorer canvas (#2473). Both `/units` and `/explorer/units/[id]`
// render this surface; the route file only contributes the entry-point
// redirect logic. Selection is read from the URL snapshot:
//   - canonical: `/explorer/units/<id>` → `selectedId = <id>` from path
//   - legacy: `?node=<id>` is rewritten by the entry-point redirect; the
//     canvas itself never reads it.
//
// All node/tab writes go through `window.history.replaceState` + the
// Explorer URL-change event so the canvas stays mounted across clicks
// (App Router navigations would tear down the tree on every selection).

import {
  useCallback,
  useEffect,
  useMemo,
  useSyncExternalStore,
} from "react";
import Link from "next/link";
import { Loader2, Plus } from "lucide-react";
import { useRouter } from "next/navigation";

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

const EXPLORER_PATH_PREFIX = "/explorer/units/";

/** Pull the selected node id from `/explorer/units/<id>` pathnames. */
function selectedIdFromPathname(pathname: string): string | undefined {
  if (!pathname.startsWith(EXPLORER_PATH_PREFIX)) return undefined;
  const tail = pathname.slice(EXPLORER_PATH_PREFIX.length);
  if (!tail) return undefined;
  const slash = tail.indexOf("/");
  const id = slash === -1 ? tail : tail.slice(0, slash);
  return id ? decodeURIComponent(id) : undefined;
}

export function ExplorerSurface() {
  const router = useRouter();
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

  // #2266: human nodes don't live in the tenant tree — bounce to the
  // dedicated `/humans/<guid>` route. Preserves any active tab so the
  // detail-pane chrome lands on the right surface.
  useEffect(() => {
    if (!selectedId) return;
    const humanGuid = parseHumanSelection(selectedId);
    if (humanGuid === null) return;
    const qs = tab ? `?tab=${encodeURIComponent(tab)}` : "";
    router.replace(`/humans/${encodeURIComponent(humanGuid)}${qs}`);
  }, [router, selectedId, tab]);

  const treeQuery = useTenantTree();

  const writeUrl = useCallback(
    (next: { node?: string; tab?: TabName }) => {
      // Node selection lives in the path segment; tab is view state in
      // `?tab=`. Carry the existing tab forward unless the caller is
      // explicitly switching it (and clear it on a node-only switch so
      // a stale tab from a different node kind doesn't ride along —
      // #1704).
      const targetNode =
        next.node !== undefined
          ? toExplorerPathSegment(next.node)
          : selectedId
            ? toExplorerPathSegment(selectedId)
            : "";

      const params = new URLSearchParams();
      if (next.tab !== undefined) {
        params.set("tab", next.tab);
      } else if (next.node === undefined && tab) {
        // Tab unchanged, node unchanged — preserve tab.
        params.set("tab", tab);
      }
      const qs = params.toString();
      const target = targetNode
        ? `${EXPLORER_PATH_PREFIX}${encodeURIComponent(targetNode)}${qs ? `?${qs}` : ""}`
        : `${EXPLORER_PATH_PREFIX}${qs ? `?${qs}` : ""}`;
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
 * when matched, `null` otherwise so the caller can fall through to
 * unit-tree resolution.
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
  if (candidate.length === 32) {
    return `${candidate.slice(0, 8)}-${candidate.slice(8, 12)}-${candidate.slice(12, 16)}-${candidate.slice(16, 20)}-${candidate.slice(20)}`;
  }
  return candidate;
}
