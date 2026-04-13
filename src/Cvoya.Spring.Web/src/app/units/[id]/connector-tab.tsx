"use client";

import { useCallback, useEffect, useState } from "react";
import { Github, Link2, Settings } from "lucide-react";

import { Badge } from "@/components/ui/badge";
import { Button } from "@/components/ui/button";
import {
  Card,
  CardContent,
  CardHeader,
  CardTitle,
} from "@/components/ui/card";
import { Skeleton } from "@/components/ui/skeleton";
import { useToast } from "@/components/ui/toast";
import { getConnectorComponent } from "@/connectors/registry";
import { api } from "@/lib/api/client";
import type {
  ConnectorTypeResponse,
  UnitConnectorPointerResponse,
} from "@/lib/api/types";

export interface ConnectorTabProps {
  unitId: string;
}

/**
 * Generic Connector tab. Fetches the unit's active binding pointer
 * (`/api/v1/units/{id}/connector`), then delegates rendering to the
 * connector-specific component registered under the active typeSlug.
 *
 * When the unit is unbound, renders a chooser that lists every
 * server-registered connector type so the user can pick one. Selecting a
 * connector swaps the tab into the connector's own form — saving from
 * inside that form atomically binds the type AND writes the config.
 */
export function ConnectorTab({ unitId }: ConnectorTabProps) {
  const { toast } = useToast();
  const [loading, setLoading] = useState(true);
  const [loadError, setLoadError] = useState<string | null>(null);
  const [pointer, setPointer] =
    useState<UnitConnectorPointerResponse | null>(null);
  const [connectors, setConnectors] = useState<ConnectorTypeResponse[]>([]);
  const [pendingSlug, setPendingSlug] = useState<string | null>(null);

  const refresh = useCallback(async () => {
    try {
      const [ptr, list] = await Promise.all([
        api.getUnitConnector(unitId),
        api.listConnectors(),
      ]);
      setPointer(ptr ?? null);
      setConnectors(list);
      setLoadError(null);
    } catch (err) {
      setLoadError(err instanceof Error ? err.message : String(err));
    }
  }, [unitId]);

  useEffect(() => {
    let cancelled = false;
    setLoading(true);
    refresh().finally(() => {
      if (!cancelled) setLoading(false);
    });
    return () => {
      cancelled = true;
    };
  }, [refresh]);

  const handleUnbind = async () => {
    try {
      await api.clearUnitConnector(unitId);
      setPointer(null);
      setPendingSlug(null);
      toast({ title: "Connector cleared" });
    } catch (err) {
      toast({
        title: "Failed to clear connector",
        description: err instanceof Error ? err.message : String(err),
        variant: "destructive",
      });
    }
  };

  if (loading) {
    return (
      <Card>
        <CardContent className="space-y-3 p-6">
          <Skeleton className="h-4 w-40" />
          <Skeleton className="h-10" />
        </CardContent>
      </Card>
    );
  }

  if (loadError) {
    return (
      <Card>
        <CardContent className="p-6 text-sm text-destructive">
          {loadError}
        </CardContent>
      </Card>
    );
  }

  const activeSlug = pendingSlug ?? pointer?.typeSlug ?? null;
  const Component = activeSlug ? getConnectorComponent(activeSlug) : undefined;

  if (activeSlug && Component) {
    const connectorMeta = connectors.find((c) => c.typeSlug === activeSlug);
    return (
      <div className="space-y-3">
        <div className="flex items-center justify-between rounded-md border border-border bg-card px-3 py-2 text-sm">
          <div className="flex items-center gap-2">
            <Link2 className="h-4 w-4 text-muted-foreground" />
            <span>
              Active connector:{" "}
              <strong>{connectorMeta?.displayName ?? activeSlug}</strong>
            </span>
            {pointer && <Badge variant="outline">bound</Badge>}
            {pendingSlug && !pointer && (
              <Badge variant="outline">not saved</Badge>
            )}
          </div>
          {pointer && (
            <Button
              size="sm"
              variant="outline"
              onClick={handleUnbind}
              aria-label="Unbind connector"
            >
              Unbind
            </Button>
          )}
        </div>
        <Component unitId={unitId} />
      </div>
    );
  }

  if (activeSlug && !Component) {
    return (
      <Card>
        <CardHeader>
          <CardTitle className="flex items-center gap-2">
            <Settings className="h-5 w-5" />
            Connector: {activeSlug}
            <Badge variant="outline" className="ml-2">
              UI unavailable
            </Badge>
          </CardTitle>
        </CardHeader>
        <CardContent className="space-y-2 text-sm text-muted-foreground">
          <p>
            The server is bound to a connector (<code>{activeSlug}</code>) that
            this web build does not ship a UI for. Configuration is still
            available directly against{" "}
            <code>/api/v1/connectors/{activeSlug}/units/{unitId}/config</code>.
          </p>
        </CardContent>
      </Card>
    );
  }

  // Unbound state — show a chooser.
  return (
    <Card>
      <CardHeader>
        <CardTitle className="flex items-center gap-2">
          <Github className="h-5 w-5" /> Connector
          <Badge variant="outline" className="ml-2">
            Not configured
          </Badge>
        </CardTitle>
      </CardHeader>
      <CardContent className="space-y-4">
        <p className="text-sm text-muted-foreground">
          This unit is not wired to any connector yet. Pick one below to
          open its configuration form.
        </p>
        <div className="grid gap-2 sm:grid-cols-2">
          {connectors.length === 0 && (
            <p className="col-span-full text-sm text-muted-foreground">
              No connector types are registered on the server.
            </p>
          )}
          {connectors.map((c) => {
            const hasUi = !!getConnectorComponent(c.typeSlug);
            return (
              <button
                key={c.typeId}
                type="button"
                onClick={() => hasUi && setPendingSlug(c.typeSlug)}
                disabled={!hasUi}
                className="flex flex-col items-start rounded-md border border-border p-3 text-left hover:bg-muted disabled:opacity-60"
              >
                <span className="font-medium">{c.displayName}</span>
                <span className="text-xs text-muted-foreground">
                  {c.description}
                </span>
                {!hasUi && (
                  <span className="mt-1 text-xs">
                    <Badge variant="outline">UI unavailable</Badge>
                  </span>
                )}
              </button>
            );
          })}
        </div>
      </CardContent>
    </Card>
  );
}
