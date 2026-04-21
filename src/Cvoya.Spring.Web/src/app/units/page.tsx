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
import { AlertCircle, Loader2 } from "lucide-react";
import { useRouter, useSearchParams } from "next/navigation";

import { Card, CardContent } from "@/components/ui/card";
import { UnitExplorer } from "@/components/units/unit-explorer";
import type { TabName, TreeNode } from "@/components/units/aggregate";
import { useTenantTree } from "@/lib/api/queries";
import type { ValidatedTenantTreeNode } from "@/lib/api/validate-tenant-tree";

function UnitExplorerRoute() {
  const router = useRouter();
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
    router.replace(qs ? `?${qs}` : "?", { scroll: false });
  };

  return (
    <div
      data-testid="unit-explorer-route"
      className="h-[calc(100vh-6rem)] min-h-[480px]"
    >
      <UnitExplorer
        tree={tree}
        selectedId={selectedId}
        onSelectNode={(id) => writeUrl({ node: id })}
        tab={tab ?? undefined}
        onTabChange={(id, nextTab) => writeUrl({ node: id, tab: nextTab })}
      />
    </div>
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
