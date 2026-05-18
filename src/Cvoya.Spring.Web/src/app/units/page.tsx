"use client";

// Canonical Explorer surface (EXP-route, umbrella #815). `/units` is
// the single entry point for browsing units + agents; selection and
// the active tab live in the URL (`?node=<id>&tab=<Tab>`) so Cmd-K
// teleport and deeplinks round-trip through the same query string.
//
// The legacy `/units` list + `/units/[id]` detail views are retired
// one wave at a time by the DEL-* sub-issues — the physical detail
// route stays until `DEL-units-id` lands because its tabs still host
// content the EXP-tab-unit-* issues are migrating into the Explorer.

import { Suspense, useCallback, useEffect, useMemo, useSyncExternalStore } from "react";
import Link from "next/link";
import { Loader2, Plus } from "lucide-react";
import { usePathname, useRouter, useSearchParams } from "next/navigation";

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
// until a user actually browses to `/units`.
import "@/components/units/tabs/register-all";

function UnitExplorerRoute() {
  const pathname = usePathname();
  const router = useRouter();
  const routerSearchParams = useSearchParams();
  const historySearch = useSyncExternalStore(
    subscribeExplorerUrl,
    getExplorerUrlSnapshot,
    getServerExplorerUrlSnapshot,
  );
  const routerSearch = routerSearchParams.toString();
  const searchParams = useMemo(
    () => new URLSearchParams(historySearch || routerSearch),
    [historySearch, routerSearch],
  );

  const selectedId = searchParams.get("node") ?? undefined;
  const tab = (searchParams.get("tab") as TabName | null) ?? undefined;

  // #2266: Explorer routing accepts `?node=human:<guid>` so call
  // sites that don't know which subject they're addressing can
  // funnel through `/units`. Humans don't live in the tenant tree
  // (they're not addressable via the unit graph in v0.1, see
  // docs/concepts/humans.md), so the entry point bounces to the
  // dedicated `/humans/<guid>` route — preserving any active `tab`
  // and surfacing the same Detail Pane chrome there.
  useEffect(() => {
    if (!selectedId) return;
    const humanGuid = parseHumanSelection(selectedId);
    if (humanGuid === null) return;
    const qs = tab
      ? `?tab=${encodeURIComponent(tab)}`
      : "";
    router.replace(`/humans/${encodeURIComponent(humanGuid)}${qs}`);
  }, [router, selectedId, tab]);

  const treeQuery = useTenantTree();

  // Hooks must be declared before any early return (#1704, react-hooks/rules-of-hooks).
  // `writeUrl`, `handleSelectNode`, and `handleTabChange` only depend on URL
  // state that is available on every render path.
  const writeUrl = useCallback(
    (next: { node?: string; tab?: TabName }) => {
      const params = new URLSearchParams(searchParams.toString());
      if (next.node !== undefined) {
        params.set("node", next.node);
        // #1704: clear a stale `tab` when only switching the node. Keeping
        // the old tab across a node switch makes `DetailPane` see an invalid
        // tab and fire its correction effect with a potentially-stale
        // `selectedId` closure, which can overwrite a subsequent click.
        if (next.tab === undefined) params.delete("tab");
      }
      if (next.tab !== undefined) params.set("tab", next.tab);
      const qs = params.toString();
      const target = qs ? `${pathname}?${qs}` : pathname;
      // The Explorer's node/tab state is fully client-owned. Using the
      // App Router here creates an RSC navigation for every tab click;
      // native history keeps the URL contract without letting a pending
      // route transition pin the visible tab.
      window.history.replaceState(null, "", target);
      dispatchExplorerUrlChange();
    },
    [searchParams, pathname],
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
      <UnitsPageHeader />
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
 * CTA that mirrors the dashboard's button (#1069). The per-node
 * "Engagement" affordance lives on `<UnitPaneActions>` (#1463/#1464), so
 * there is no ambient page-level engagement button here (#1461/#1462).
 *
 * No heading element — `<DetailPane>` ships the page's only `<h1>` (the
 * selected node's name), and DESIGN.md §14 caps each page at one `<h1>`.
 */
function UnitsPageHeader() {
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
 *
 * Accepts both the canonical `human:<guid>` short-form (the same shape
 * the platform's HumanActor address scheme uses) and the navigation
 * `human://<guid>` form for symmetry with `agent://` / `unit://`. Both
 * surface the same underlying address; the address-scheme prefix is
 * the only signal the Explorer needs to redirect to the dedicated
 * `/humans/<guid>` route.
 *
 * Returns the Guid string when the input matches, `null` otherwise so
 * the caller can fall through to the existing unit-tree resolution
 * path.
 */
function parseHumanSelection(raw: string): string | null {
  const candidate = raw.startsWith("human://")
    ? raw.slice("human://".length)
    : raw.startsWith("human:")
      ? raw.slice("human:".length)
      : null;
  if (candidate === null) return null;
  // The Guid is the slug; anything past a `/` or `?` is unexpected
  // and we conservatively reject. Lower-case + dashed canonical Guid
  // is the wire form `human:<hex>` is normalised to elsewhere (see
  // `lib/utils.ts`); accept either short-hex or dashed.
  const UUID_RE =
    /^[0-9a-f]{8}-?[0-9a-f]{4}-?[0-9a-f]{4}-?[0-9a-f]{4}-?[0-9a-f]{12}$/i;
  if (!UUID_RE.test(candidate)) return null;
  // Normalise no-dash compact hex (`8adb6dd4bb7b4998a413e1d1528bf71b`) to
  // the dashed canonical form the `/humans/[id]` route validates against.
  if (candidate.length === 32) {
    return `${candidate.slice(0, 8)}-${candidate.slice(8, 12)}-${candidate.slice(12, 16)}-${candidate.slice(16, 20)}-${candidate.slice(20)}`;
  }
  return candidate;
}

export default function UnitsPage() {
  // `useSearchParams` requires a Suspense boundary in the App Router —
  // the skeleton below handles both the Next-level Suspense and the
  // tree query's own loading state.
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
      <UnitExplorerRoute />
    </Suspense>
  );
}
