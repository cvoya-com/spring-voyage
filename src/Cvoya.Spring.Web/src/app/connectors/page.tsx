"use client";

/**
 * /connectors — top-level connector catalog (#449).
 *
 * Mirrors the data the CLI's `spring connector catalog` consumes:
 * the generic `/api/v1/connectors` endpoint feeds both surfaces. Each
 * card deep-links to `/connectors/[type]` — the same destination the
 * unit Connector tab points at when an "active connector" header is
 * shown — so the catalog is also a hub for "what binds this connector?"
 *
 * Design contract: docs/design/portal-exploration.md § 3.2 lists
 * Connectors as a primary nav entry; the empty-state pattern matches
 * `/packages` so operators see the same "install more packages" hint
 * regardless of which catalog is empty.
 */

import Link from "next/link";
import { ArrowRight, Plug } from "lucide-react";

import { Card, CardContent } from "@/components/ui/card";
import { Skeleton } from "@/components/ui/skeleton";
import { useConnectorTypes } from "@/lib/api/queries";
import type { ConnectorTypeResponse } from "@/lib/api/types";
import { cn } from "@/lib/utils";

export default function ConnectorsListPage() {
  const query = useConnectorTypes();
  const connectors = query.data ?? [];
  const loading = query.isPending;

  return (
    <div className="space-y-6">
      <div>
        <h1 className="flex items-center gap-2 text-2xl font-bold">
          <Plug className="h-5 w-5" /> Connectors
        </h1>
        <p className="text-sm text-muted-foreground">
          Every connector type registered on this server. Mirrors{" "}
          <code className="rounded bg-muted px-1 py-0.5 text-xs">
            spring connector catalog
          </code>
          .
        </p>
      </div>

      {loading ? (
        <div className="grid grid-cols-1 gap-3 sm:grid-cols-2 lg:grid-cols-3">
          <Skeleton className="h-32" />
          <Skeleton className="h-32" />
          <Skeleton className="h-32" />
        </div>
      ) : query.error ? (
        <Card>
          <CardContent className="p-6">
            <p className="text-sm text-destructive" role="alert">
              Failed to load connectors: {query.error.message}
            </p>
          </CardContent>
        </Card>
      ) : connectors.length === 0 ? (
        <Card>
          <CardContent className="space-y-2 p-6 text-center">
            <Plug className="mx-auto h-10 w-10 text-muted-foreground" />
            <p className="text-sm text-muted-foreground">
              No connector types registered. Install a connector
              package and restart the host to make it appear here. See{" "}
              <Link
                href="/packages"
                className="text-primary hover:underline"
              >
                Packages
              </Link>{" "}
              for the catalog of installed packages.
            </p>
          </CardContent>
        </Card>
      ) : (
        <div className="grid grid-cols-1 gap-3 sm:grid-cols-2 lg:grid-cols-3">
          {connectors.map((c) => (
            <ConnectorCard key={c.typeId} connector={c} />
          ))}
        </div>
      )}
    </div>
  );
}

function ConnectorCard({ connector }: { connector: ConnectorTypeResponse }) {
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
