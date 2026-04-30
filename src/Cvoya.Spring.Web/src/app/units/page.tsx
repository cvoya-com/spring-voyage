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

import { Suspense } from "react";
import Link from "next/link";
import { AlertCircle, Loader2, Plus } from "lucide-react";
import { usePathname, useRouter, useSearchParams } from "next/navigation";

import { Card, CardContent } from "@/components/ui/card";
import { UnitExplorer } from "@/components/units/unit-explorer";
import type { TabName, TreeNode } from "@/components/units/aggregate";
import { useTenantTree } from "@/lib/api/queries";
import type { ValidatedTenantTreeNode } from "@/lib/api/validate-tenant-tree";

// Side-effect import — each tab module calls `registerTab(...)` at
// module top-level (see `src/components/units/tabs/register-all.ts`),
// so importing the barrel here is what wires the EXP-tab-* content
// into the registry consumed by `<DetailPane>`. Keeping the import
// local to the Explorer route means hosted tab bundles stay lazy
// until a user actually browses to `/units`.
import "@/components/units/tabs/register-all";

function UnitExplorerRoute() {
  const router = useRouter();
  const pathname = usePathname();
  const searchParams = useSearchParams();

  const selectedId = searchParams.get("node") ?? undefined;
  const tab = (searchParams.get("tab") as TabName | null) ?? undefined;

  const treeQuery = useTenantTree();

  if (treeQuery.isError) {
    return (
      <Card
        role="alert"
        data-testid="unit-explorer-error"
        className="border-destructive/50 bg-destructive/5"
      >
        <CardContent className="flex items-start gap-2 p-4 text-sm text-destructive">
          <AlertCircle className="h-4 w-4 shrink-0" aria-hidden="true" />
          <div>
            <p className="font-medium">Couldn&apos;t load the tenant tree.</p>
            <p className="text-xs text-destructive/80">
              {treeQuery.error instanceof Error
                ? treeQuery.error.message
                : "Unknown error"}
            </p>
          </div>
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

  const writeUrl = (next: { node?: string; tab?: TabName }) => {
    const params = new URLSearchParams(searchParams.toString());
    if (next.node !== undefined) params.set("node", next.node);
    if (next.tab !== undefined) params.set("tab", next.tab);
    const qs = params.toString();
    // #1039: Next.js 16's `router.replace("?foo=bar")` with a bare
    // query-only relative URL doesn't update the canonical URL — the
    // reconciler's `replaceState` call fires with the stale query, so
    // the URL (and controlled `tab`/`node` props derived from it) snap
    // back to the prior value the moment React commits. Passing the
    // full pathname alongside the query restores the intended navigation.
    router.replace(qs ? `${pathname}?${qs}` : pathname, { scroll: false });
  };

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
          onSelectNode={(id) => writeUrl({ node: id })}
          tab={tab ?? undefined}
          onTabChange={(id, nextTab) => writeUrl({ node: id, tab: nextTab })}
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
