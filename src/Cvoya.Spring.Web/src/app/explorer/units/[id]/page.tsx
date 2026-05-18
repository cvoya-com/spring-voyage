"use client";

/**
 * `/explorer/units/[id]` — canonical Explorer entry point for a unit or
 * agent node (#2473).
 *
 * The `[id]` segment carries a no-dash UUID (32 hex chars). This page
 * renders the same Explorer that `/units` hosts but with the node
 * pre-selected via a URL dispatch on mount. The `/units` page now
 * redirects legacy `?node=<id>` URLs here, and all link-building sites
 * produce this path directly.
 *
 * Tab state stays in `?tab=<Tab>` (view state, not resource identity).
 */

import { Suspense, useCallback, useEffect, useMemo, useSyncExternalStore } from "react";
import Link from "next/link";
import { Loader2, Plus } from "lucide-react";
import { useParams, usePathname, useRouter, useSearchParams } from "next/navigation";

import { ApiErrorMessage } from "@/components/ui/api-error-message";
import { Card, CardContent } from "@/components/ui/card";
import { UnitExplorer } from "@/components/units/unit-explorer";
import type { TabName, TreeNode } from "@/components/units/aggregate";
import { useTenantTree } from "@/lib/api/queries";
import type { ValidatedTenantTreeNode } from "@/lib/api/validate-tenant-tree";
import {
  dispatchExplorerUrlChange,
  getExplorerUrlSnapshot,
  getServerExplorerUrlSnapshot,
  subscribeExplorerUrl,
} from "@/lib/explorer-url";

// Side-effect import — each tab module calls `registerTab(...)` at
// module top-level (see `src/components/units/tabs/register-all.ts`),
// so importing the barrel here is what wires the EXP-tab-* content
// into the registry consumed by `<DetailPane>`. Keeping the import
// local to the Explorer route means hosted tab bundles stay lazy
// until a user actually browses here.
import "@/components/units/tabs/register-all";

/**
 * Strip dashes from a UUID to produce the no-dash path segment form.
 */
function toNoDash(id: string): string {
  return id.replace(/-/g, "");
}

function ExplorerUnitsRoute() {
  const params = useParams<{ id: string }>();
  const nodeId = params.id ?? "";
  const pathname = usePathname();
  const router = useRouter();
  const routerSearchParams = useSearchParams();
  const historyUrl = useSyncExternalStore(
    subscribeExplorerUrl,
    getExplorerUrlSnapshot,
    getServerExplorerUrlSnapshot,
  );

  // Derive search params from history URL (which includes pathname) or
  // router search params.
  const searchParams = useMemo(() => {
    if (historyUrl) {
      const qIdx = historyUrl.indexOf("?");
      return new URLSearchParams(qIdx >= 0 ? historyUrl.slice(qIdx) : "");
    }
    return new URLSearchParams(routerSearchParams.toString());
  }, [historyUrl, routerSearchParams]);

  const tab = (searchParams.get("tab") as TabName | null) ?? undefined;

  // #2473: the selected node id is the path segment, not a query param.
  // The path segment is a no-dash UUID; keep it as-is for the Explorer.
  const selectedId = nodeId || undefined;

  const treeQuery = useTenantTree();

  const writeUrl = useCallback(
    (next: { node?: string; tab?: TabName }) => {
      if (next.node !== undefined) {
        // Write node selection as a path segment.
        const nodePath = toNoDash(next.node);
        const tabPart = next.tab
          ? `?tab=${encodeURIComponent(next.tab)}`
          : "";
        const target = `/explorer/units/${encodeURIComponent(nodePath)}${tabPart}`;
        window.history.replaceState(null, "", target);
      } else if (next.tab !== undefined) {
        // Tab-only update: preserve current path, update tab param.
        const tabParams = new URLSearchParams(searchParams.toString());
        tabParams.set("tab", next.tab);
        const currentPath = historyUrl
          ? historyUrl.split("?")[0]
          : pathname;
        const target = `${currentPath}?${tabParams.toString()}`;
        window.history.replaceState(null, "", target);
      }
      // The Explorer's node/tab state is fully client-owned. Using the
      // App Router here creates an RSC navigation for every tab click;
      // native history keeps the URL contract without letting a pending
      // route transition pin the visible tab.
      dispatchExplorerUrlChange();
    },
    [searchParams, pathname, historyUrl],
  );

  // #2266 / #2473: if the node is a human, bounce to /humans/<guid>.
  useEffect(() => {
    if (!selectedId) return;
    const humanGuid = parseHumanSelection(selectedId);
    if (humanGuid === null) return;
    const qs = tab ? `?tab=${encodeURIComponent(tab)}` : "";
    router.replace(`/humans/${encodeURIComponent(humanGuid)}${qs}`);
  }, [router, selectedId, tab]);

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
        <Loader2
          className="mr-2 h-4 w-4 animate-spin"
          aria-hidden="true"
        />
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

/**
 * Header bar above the Explorer surface — a single primary "New unit"
 * CTA that mirrors the dashboard's button (#1069).
 */
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

/**
 * The validator emits a `ValidatedTenantTreeNode` with `kind` already
 * narrowed to the {@link TreeNode} union; cast through the structurally
 * compatible shape so `<UnitExplorer>` gets the union type it expects
 * without an extra per-node walk.
 */
function adaptValidatedNode(node: ValidatedTenantTreeNode): TreeNode {
  return node as unknown as TreeNode;
}

/**
 * #2266: parse the Guid out of an Explorer `?node=human:<guid>` value.
 * Accepts both the canonical `human:<guid>` and `human://<guid>` forms.
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

export default function ExplorerUnitsPage() {
  // `useSearchParams` requires a Suspense boundary in the App Router.
  return (
    <Suspense
      fallback={
        <div
          role="status"
          aria-live="polite"
          data-testid="unit-explorer-loading"
          className="flex h-full min-h-[50vh] items-center justify-center text-sm text-muted-foreground"
        >
          <Loader2
            className="mr-2 h-4 w-4 animate-spin"
            aria-hidden="true"
          />
          Loading tenant tree…
        </div>
      }
    >
      <ExplorerUnitsRoute />
    </Suspense>
  );
}
