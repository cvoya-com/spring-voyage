"use client";

/**
 * /connectors — top-level connector catalog (#449) + health tab (#868).
 *
 * Mirrors the data the CLI's `spring connector catalog` consumes: the
 * generic `/api/v1/connectors` endpoint feeds both surfaces. Post-#714
 * this page lists only the connectors installed on the current tenant
 * (aligned with the agent-runtimes surface); a connector registered
 * with the host but not installed on the tenant is invisible here and
 * in the unit Connector tab. Each card deep-links to `/connectors/[type]`
 * — the same destination the unit Connector tab points at when an
 * "active connector" header is shown — so the catalog is also a hub for
 * "what binds this connector?"
 *
 * Per the v2 IA rework (umbrella #815 § 9), the legacy `/admin/connectors`
 * Health view is absorbed here as a second tab. Tab state is mirrored
 * into `?tab=` so deep links survive refresh; the bare `/connectors`
 * URL always lands on the catalog. Mutations (install, uninstall,
 * configure, credential validation) still ride `spring connector …`
 * per the AGENTS.md carve-out — the portal is visibility-only.
 *
 * Design contract: docs/design/portal-exploration.md § 3.2 lists
 * Connectors as a primary nav entry; the empty-state pattern matches
 * `/packages` so operators see the same "install more packages" hint
 * regardless of which catalog is empty.
 */

import { Suspense, useCallback } from "react";
import Link from "next/link";
import { useRouter, useSearchParams } from "next/navigation";
import { ArrowRight, Plug } from "lucide-react";

import { Card, CardContent } from "@/components/ui/card";
import { Skeleton } from "@/components/ui/skeleton";
import {
  Tabs,
  TabsContent,
  TabsList,
  TabsTrigger,
} from "@/components/ui/tabs";
import { ConnectorHealthPanel } from "@/components/connectors/health-panel";
import { useConnectorTypes } from "@/lib/api/queries";
import type { InstalledConnectorResponse } from "@/lib/api/types";
import { cn } from "@/lib/utils";

const DEFAULT_TAB = "catalog";
const VALID_TABS = new Set(["catalog", "health"]);

function parseTab(raw: string | null): string {
  if (raw && VALID_TABS.has(raw)) return raw;
  return DEFAULT_TAB;
}

function ConnectorsContent() {
  const router = useRouter();
  const searchParams = useSearchParams();
  const activeTab = parseTab(searchParams.get("tab"));

  // Mirror active tab into `?tab=` so deep links survive refresh; the
  // default tab collapses to no query so the bare `/connectors` URL
  // remains the canonical catalog entry. Matches the pattern used by
  // the unit-detail + agent-detail tab bars.
  const setActiveTab = useCallback(
    (next: string) => {
      const params = new URLSearchParams(searchParams.toString());
      if (next === DEFAULT_TAB) {
        params.delete("tab");
      } else {
        params.set("tab", next);
      }
      const qs = params.toString();
      router.replace(qs ? `?${qs}` : "?", { scroll: false });
    },
    [router, searchParams],
  );

  return (
    <div className="space-y-6">
      <div>
        <h1 className="flex items-center gap-2 text-2xl font-bold">
          <Plug className="h-5 w-5" aria-hidden="true" /> Connectors
        </h1>
        <p className="text-sm text-muted-foreground">
          Every connector installed on the current tenant. Mirrors{" "}
          <code className="rounded bg-muted px-1 py-0.5 text-xs">
            spring connector catalog
          </code>
          .
        </p>
      </div>

      <Tabs
        defaultValue={DEFAULT_TAB}
        value={activeTab}
        onValueChange={setActiveTab}
      >
        <TabsList aria-label="Connector sections">
          <TabsTrigger value="catalog">Catalog</TabsTrigger>
          <TabsTrigger value="health">Health</TabsTrigger>
        </TabsList>

        <TabsContent value="catalog" className="space-y-6">
          <ConnectorCatalog />
        </TabsContent>

        <TabsContent value="health" className="space-y-6">
          <ConnectorHealthPanel />
        </TabsContent>
      </Tabs>
    </div>
  );
}

function ConnectorCatalog() {
  const query = useConnectorTypes();
  const connectors = query.data ?? [];
  const loading = query.isPending;

  if (loading) {
    return (
      <div className="grid grid-cols-1 gap-3 sm:grid-cols-2 lg:grid-cols-3">
        <Skeleton className="h-32" />
        <Skeleton className="h-32" />
        <Skeleton className="h-32" />
      </div>
    );
  }
  if (query.error) {
    return (
      <Card>
        <CardContent className="p-6">
          <p className="text-sm text-destructive" role="alert">
            Failed to load connectors: {query.error.message}
          </p>
        </CardContent>
      </Card>
    );
  }
  if (connectors.length === 0) {
    return (
      <Card>
        <CardContent className="space-y-2 p-6 text-center">
          <Plug className="mx-auto h-10 w-10 text-muted-foreground" aria-hidden="true" />
          <p className="text-sm text-muted-foreground">
            No connectors installed on this tenant. Install one with{" "}
            <code className="rounded bg-muted px-1 py-0.5 text-xs">
              spring connector install &lt;slug&gt;
            </code>{" "}
            — run{" "}
            <code className="rounded bg-muted px-1 py-0.5 text-xs">
              spring connector catalog
            </code>{" "}
            from the CLI to see available types, or browse{" "}
            <Link
              href="/packages"
              className="text-primary hover:underline"
            >
              Packages
            </Link>{" "}
            to install a connector-bearing package.
          </p>
        </CardContent>
      </Card>
    );
  }
  return (
    <div className="grid grid-cols-1 gap-3 sm:grid-cols-2 lg:grid-cols-3">
      {connectors.map((c) => (
        <ConnectorCard key={c.typeId} connector={c} />
      ))}
    </div>
  );
}

function ConnectorCard({ connector }: { connector: InstalledConnectorResponse }) {
  const href = `/connectors/${encodeURIComponent(connector.typeSlug)}`;
  return (
    <Card
      data-testid={`connector-card-${connector.typeSlug}`}
      className={cn(
        "h-full transition-colors hover:border-primary/50 hover:bg-muted/30",
      )}
    >
      <CardContent className="p-4">
        <Link
          href={href}
          aria-label={`Open connector ${connector.displayName}`}
          className="flex items-start justify-between gap-2"
        >
          <div className="min-w-0 flex-1">
            <h3 className="truncate font-semibold">{connector.displayName}</h3>
            <p className="mt-0.5 truncate font-mono text-xs text-muted-foreground">
              {connector.typeSlug}
            </p>
            {connector.description && (
              <p className="mt-2 line-clamp-2 text-xs text-muted-foreground">
                {connector.description}
              </p>
            )}
          </div>
          <ArrowRight
            aria-hidden="true"
            className="h-4 w-4 flex-none text-muted-foreground"
          />
        </Link>
      </CardContent>
    </Card>
  );
}

export default function ConnectorsListPage() {
  // `useSearchParams` requires a Suspense boundary in the App Router
  // (the production build refuses to prerender the route otherwise).
  // The fallback mirrors the post-load skeleton shape so the page
  // doesn't jump when hydration completes.
  return (
    <Suspense
      fallback={
        <div className="space-y-4">
          <Skeleton className="h-8 w-48" />
          <div className="grid grid-cols-1 gap-3 sm:grid-cols-2 lg:grid-cols-3">
            <Skeleton className="h-32" />
            <Skeleton className="h-32" />
            <Skeleton className="h-32" />
          </div>
        </div>
      }
    >
      <ConnectorsContent />
    </Suspense>
  );
}
