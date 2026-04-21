"use client";

/**
 * Shared connector credential-health panel (#868).
 *
 * Two surfaces render this component: the legacy `/admin/connectors`
 * route and the new `/connectors?tab=health` tab on the catalog page.
 * Both reuse the same read-only view so the IA rework (umbrella #815)
 * absorbs the admin surface into `/connectors` without duplicating the
 * read-out logic. Mutations still ride the `spring connector` CLI per
 * the AGENTS.md carve-out; this surface is visibility-only.
 *
 * Cross-reference: `docs/guide/operator/connectors.md` covers the CLI
 * workflows the "Operator guide" link deep-links into.
 */

import { Plug } from "lucide-react";

import { Card, CardContent } from "@/components/ui/card";
import { Skeleton } from "@/components/ui/skeleton";
import {
  useConnectorCredentialHealth,
  useConnectorTypes,
} from "@/lib/api/queries";
import type { InstalledConnectorResponse } from "@/lib/api/types";

import {
  CliCallout,
  CredentialHealthBadge,
  Timestamp,
} from "@/components/admin/shared";

/**
 * Render the list of installed connectors with their persistent
 * credential-health row. No heading is rendered — the caller supplies
 * the surrounding layout (admin page heading or `/connectors` tab).
 */
export function ConnectorHealthPanel() {
  const query = useConnectorTypes();
  const connectors = query.data ?? [];
  const loading = query.isPending;

  return (
    <div className="space-y-6">
      <CliCallout
        cliCommand="spring connector"
        docsHref="/docs/guide/operator/connectors.md"
        docsLabel="Operator guide"
      />

      {loading ? (
        <div className="space-y-3">
          <Skeleton className="h-24" />
          <Skeleton className="h-24" />
          <Skeleton className="h-24" />
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
            <Plug
              className="mx-auto h-10 w-10 text-muted-foreground"
              aria-hidden="true"
            />
            <p className="text-sm text-muted-foreground">
              No connectors installed on this tenant. Install one with{" "}
              <code className="rounded bg-muted px-1 py-0.5 text-xs">
                spring connector install &lt;slug&gt;
              </code>
              .
            </p>
          </CardContent>
        </Card>
      ) : (
        <div
          className="space-y-3"
          data-testid="admin-connectors-list"
        >
          {connectors.map((connector) => (
            <ConnectorRow key={connector.typeId} connector={connector} />
          ))}
        </div>
      )}
    </div>
  );
}

function ConnectorRow({
  connector,
}: {
  connector: InstalledConnectorResponse;
}) {
  const healthQuery = useConnectorCredentialHealth(connector.typeSlug);
  const healthStatus = healthQuery.data?.status ?? null;
  const lastChecked = healthQuery.data?.lastChecked ?? null;
  const lastError = healthQuery.data?.lastError ?? null;

  return (
    <Card
      data-testid={`admin-connector-row-${connector.typeSlug}`}
      className="transition-colors"
    >
      <CardContent className="space-y-3 p-4">
        <div className="flex flex-wrap items-start justify-between gap-2">
          <div className="min-w-0">
            <div className="flex flex-wrap items-center gap-2">
              <h2 className="truncate text-base font-semibold">
                {connector.displayName}
              </h2>
              <code
                className="rounded bg-muted px-1 py-0.5 font-mono text-xs text-muted-foreground"
                aria-label="Connector slug"
              >
                {connector.typeSlug}
              </code>
            </div>
            {connector.description && (
              <p className="mt-1 line-clamp-2 text-xs text-muted-foreground">
                {connector.description}
              </p>
            )}
            <p className="mt-1 text-xs text-muted-foreground">
              Installed <Timestamp value={connector.installedAt} />
            </p>
          </div>
          <div
            className="flex flex-col items-end gap-1 text-xs"
            aria-label={`Credential health for ${connector.displayName}`}
          >
            <CredentialHealthBadge
              status={healthStatus}
              data-testid={`admin-connector-health-${connector.typeSlug}`}
            />
            {lastChecked && (
              <span className="text-muted-foreground">
                Checked <Timestamp value={lastChecked} />
              </span>
            )}
          </div>
        </div>

        {lastError && healthStatus !== "Valid" && (
          <p className="rounded-md border border-destructive/30 bg-destructive/5 px-3 py-2 text-xs text-destructive">
            <span className="font-semibold">Last error:</span> {lastError}
          </p>
        )}
      </CardContent>
    </Card>
  );
}
