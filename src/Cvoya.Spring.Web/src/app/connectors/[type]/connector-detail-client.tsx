"use client";

/**
 * /connectors/[type] — connector detail (#449).
 *
 * Section breakdown matches what `spring connector catalog` and
 * `spring connector show` surface together: identity (slug, typeId,
 * description), what it binds to (the `{unitId}` config URL template
 * and actions base URL), the JSON Schema describing the per-unit
 * config body, and a list of units that currently bind this
 * connector. Every unit row deep-links into the unit's Connector tab
 * so the operator can pivot from "who uses this?" to "what does the
 * config look like?" in one click.
 *
 * Cross-page contract: the unit Connector tab links here for every
 * connector type the chooser surfaces — see
 * `src/app/units/[id]/connector-tab.tsx`.
 */

import Link from "next/link";
import {
  ArrowRight,
  FileJson,
  Link2,
  Network,
  Plug,
  Settings,
} from "lucide-react";
import type { ReactNode } from "react";

import { Breadcrumbs } from "@/components/breadcrumbs";
import { Badge } from "@/components/ui/badge";
import { Card, CardContent, CardHeader, CardTitle } from "@/components/ui/card";
import { Skeleton } from "@/components/ui/skeleton";
import {
  useConnector,
  useConnectorBindings,
  useConnectorConfigSchema,
} from "@/lib/api/queries";

interface Props {
  slugOrId: string;
}

export default function ConnectorDetailClient({ slugOrId }: Props) {
  const query = useConnector(slugOrId);
  const connector = query.data;

  if (query.isPending) {
    return (
      <div className="space-y-4">
        <Skeleton className="h-4 w-48" />
        <Skeleton className="h-8 w-48" />
        <Skeleton className="h-48" />
      </div>
    );
  }

  if (query.error) {
    return (
      <div className="space-y-4">
        <Breadcrumbs
          items={[
            { label: "Connectors", href: "/connectors" },
            { label: slugOrId },
          ]}
        />
        <Card>
          <CardContent className="p-6">
            <p className="text-sm text-destructive" role="alert">
              Failed to load connector: {query.error.message}
            </p>
          </CardContent>
        </Card>
      </div>
    );
  }

  if (connector === null || connector === undefined) {
    return (
      <div className="space-y-4">
        <Breadcrumbs
          items={[
            { label: "Connectors", href: "/connectors" },
            { label: slugOrId },
          ]}
        />
        <Card>
          <CardContent className="p-6">
            <p className="text-sm text-muted-foreground">
              Connector &quot;{slugOrId}&quot; is not installed on the current
              tenant. Install it with{" "}
              <code className="rounded bg-muted px-1 py-0.5 text-xs">
                spring connector install {slugOrId}
              </code>
              .
            </p>
          </CardContent>
        </Card>
      </div>
    );
  }

  return (
    <ConnectorDetailBody connector={connector} />
  );
}

function ConnectorDetailBody({
  connector,
}: {
  connector: NonNullable<ReturnType<typeof useConnector>["data"]>;
}) {
  const slug = connector.typeSlug;
  const schemaQuery = useConnectorConfigSchema(slug);
  const bindingsQuery = useConnectorBindings(slug);
  const bindings = bindingsQuery.data ?? [];

  return (
    <div className="space-y-6">
      <Breadcrumbs
        items={[
          { label: "Connectors", href: "/connectors" },
          { label: connector.displayName },
        ]}
      />

      <div>
        <h1 className="flex items-center gap-2 text-2xl font-bold">
          <Plug className="h-5 w-5" /> {connector.displayName}
        </h1>
        <p className="mt-1 font-mono text-xs text-muted-foreground">
          {connector.typeSlug}
          <span className="mx-2 text-muted-foreground/50">·</span>
          <span title="Stable connector type id persisted with every binding.">
            {connector.typeId}
          </span>
        </p>
        {connector.description && (
          <p className="mt-2 text-sm text-muted-foreground">
            {connector.description}
          </p>
        )}
      </div>

      <Section
        title="Binds to"
        icon={<Link2 className="h-4 w-4" />}
      >
        <p className="text-sm text-muted-foreground">
          A unit binds this connector by writing its per-unit config to:
        </p>
        <pre className="overflow-x-auto rounded border border-border bg-muted p-3 text-xs">
          {connector.configUrl}
        </pre>
        <p className="text-sm text-muted-foreground">
          Connector-scoped actions are mapped under:
        </p>
        <pre className="overflow-x-auto rounded border border-border bg-muted p-3 text-xs">
          {connector.actionsBaseUrl}
        </pre>
      </Section>

      <Section
        title="Configuration schema"
        icon={<FileJson className="h-4 w-4" />}
      >
        {schemaQuery.isPending ? (
          <Skeleton className="h-32" />
        ) : schemaQuery.data == null ? (
          <p className="text-sm text-muted-foreground">
            This connector does not advertise a JSON Schema. Inspect{" "}
            <code className="rounded bg-muted px-1 py-0.5 text-xs">
              {connector.configSchemaUrl}
            </code>{" "}
            directly to see the raw response.
          </p>
        ) : (
          <pre
            data-testid="connector-config-schema"
            className="max-h-96 overflow-auto rounded border border-border bg-muted p-3 text-xs"
          >
            {JSON.stringify(schemaQuery.data, null, 2)}
          </pre>
        )}
      </Section>

      <Section
        title={`Bound units (${bindings.length})`}
        icon={<Network className="h-4 w-4" />}
      >
        {bindingsQuery.isPending ? (
          <Skeleton className="h-16" />
        ) : bindingsQuery.error ? (
          <p className="text-sm text-destructive" role="alert">
            Failed to load bound units: {bindingsQuery.error.message}
          </p>
        ) : bindings.length === 0 ? (
          <p className="text-sm text-muted-foreground">
            No units are currently bound to this connector. Open a
            unit&apos;s{" "}
            <span className="text-foreground">Connector</span> tab and
            pick &quot;{connector.displayName}&quot; to bind one.
          </p>
        ) : (
          <ul className="divide-y divide-border">
            {bindings.map((b) => (
              <li
                key={b.unitId}
                className="flex items-center justify-between gap-2 py-2 text-sm"
              >
                <div className="min-w-0 flex-1">
                  <p className="truncate font-medium">{b.unitDisplayName}</p>
                  <p className="truncate font-mono text-xs text-muted-foreground">
                    unit://{b.unitName}
                  </p>
                </div>
                <Badge variant="outline">bound</Badge>
                <Link
                  href={`/units/${encodeURIComponent(b.unitId)}`}
                  className="inline-flex items-center gap-1 text-xs text-primary hover:underline"
                  aria-label={`Open ${b.unitDisplayName} unit detail`}
                >
                  Open <ArrowRight className="h-3 w-3" />
                </Link>
              </li>
            ))}
          </ul>
        )}
      </Section>

      <Section
        title="Endpoints"
        icon={<Settings className="h-4 w-4" />}
      >
        <ul className="space-y-1 text-xs">
          <li>
            <span className="text-muted-foreground">Catalog entry:</span>{" "}
            <code className="rounded bg-muted px-1 py-0.5">
              GET /api/v1/connectors/{connector.typeSlug}
            </code>
          </li>
          <li>
            <span className="text-muted-foreground">Per-unit config:</span>{" "}
            <code className="rounded bg-muted px-1 py-0.5">
              {connector.configUrl}
            </code>
          </li>
          <li>
            <span className="text-muted-foreground">Config schema:</span>{" "}
            <code className="rounded bg-muted px-1 py-0.5">
              {connector.configSchemaUrl}
            </code>
          </li>
          <li>
            <span className="text-muted-foreground">Actions base:</span>{" "}
            <code className="rounded bg-muted px-1 py-0.5">
              {connector.actionsBaseUrl}
            </code>
          </li>
        </ul>
      </Section>
    </div>
  );
}

function Section({
  title,
  icon,
  children,
}: {
  title: string;
  icon: ReactNode;
  children: ReactNode;
}) {
  return (
    <Card>
      <CardHeader>
        <CardTitle className="flex items-center gap-2 text-base">
          {icon}
          {title}
        </CardTitle>
      </CardHeader>
      <CardContent>
        <div className="space-y-3">{children}</div>
      </CardContent>
    </Card>
  );
}
