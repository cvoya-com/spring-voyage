"use client";

/**
 * /admin/connectors — read-only admin view (#691).
 *
 * Lists the tenant's installed connectors with the persistent
 * credential-health row next to each one. Mutations (install,
 * uninstall, per-tenant configuration, credential validation) ride
 * `spring connector …` per the AGENTS.md carve-out — the end-user
 * catalog lives under `/connectors` and this surface is the
 * admin-facing health view.
 *
 * Cross-reference: `docs/user-guide/connectors.md` covers the CLI
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
} from "../admin-shared";

export default function AdminConnectorsPage() {
  const query = useConnectorTypes();
  const connectors = query.data ?? [];
  const loading = query.isPending;

  return (
    <div className="space-y-6">
      <div>
        <h1 className="flex items-center gap-2 text-2xl font-bold">
          <Plug className="h-5 w-5" aria-hidden="true" /> Connector health
        </h1>
        <p className="text-sm text-muted-foreground">
          Installed connectors on the current tenant and their
          credential-health status.
        </p>
      </div>

      <CliCallout
        cliCommand="spring connector"
        docsHref="/docs/user-guide/connectors.md"
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
