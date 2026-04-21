"use client";

/**
 * Unit expertise panel (#486). Renders two cards:
 *
 *   1. "Own expertise" — editable list that reads/writes
 *      `/api/v1/units/{id}/expertise/own` (CLI:
 *      `spring unit expertise {get|set}`).
 *   2. "Effective expertise" — read-only aggregated view composed
 *      recursively from every descendant (PR #487; CLI:
 *      `spring unit expertise aggregated`). Each row shows the origin
 *      address (agent:// or unit://) and depth from this unit so
 *      operators can see where a capability is actually declared.
 *
 * Consumed from the Unit Config → Expertise sub-tab. The read-only
 * "Expertise" summary card on the Unit Overview tab uses only the
 * aggregated hook directly (see `unit-overview.tsx`).
 */

import Link from "next/link";
import { useMemo } from "react";
import { useQueryClient } from "@tanstack/react-query";
import { GraduationCap, Layers } from "lucide-react";

import { Badge } from "@/components/ui/badge";
import {
  Card,
  CardContent,
  CardHeader,
  CardTitle,
} from "@/components/ui/card";
import { Skeleton } from "@/components/ui/skeleton";
import { api } from "@/lib/api/client";
import {
  useUnitAggregatedExpertise,
  useUnitOwnExpertise,
} from "@/lib/api/queries";
import { queryKeys } from "@/lib/api/query-keys";
import type {
  AggregatedExpertiseEntryDto,
  ExpertiseDomainDto,
} from "@/lib/api/types";

import { ExpertiseEditor } from "./expertise-editor";

interface UnitExpertisePanelProps {
  unitId: string;
}

export function UnitExpertisePanel({ unitId }: UnitExpertisePanelProps) {
  const queryClient = useQueryClient();
  const ownQuery = useUnitOwnExpertise(unitId);
  const aggregatedQuery = useUnitAggregatedExpertise(unitId);

  const domains = ownQuery.data ?? [];
  const aggregated = aggregatedQuery.data;

  const handleSaveOwn = async (
    next: ExpertiseDomainDto[],
  ): Promise<ExpertiseDomainDto[]> => {
    const updated = await api.setUnitOwnExpertise(unitId, next);
    queryClient.setQueryData(queryKeys.units.ownExpertise(unitId), updated);
    // The aggregated view on this unit and every ancestor needs to
    // refetch — the server invalidates its own aggregator cache, but
    // the TanStack cache is still authoritative on the client. Blanket-
    // invalidate the aggregated surface so sibling unit pages pick up
    // the change too.
    await queryClient.invalidateQueries({
      queryKey: queryKeys.units.aggregatedExpertise(unitId),
    });
    await queryClient.invalidateQueries({
      queryKey: ["units", "aggregatedExpertise"],
    });
    await queryClient.invalidateQueries({ queryKey: queryKeys.directory.all });
    return updated;
  };

  return (
    <div
      className="grid grid-cols-1 gap-4 lg:grid-cols-2"
      data-testid="unit-expertise-panel"
    >
      <Card>
        <CardHeader>
          <CardTitle className="flex items-center gap-2">
            <GraduationCap className="h-4 w-4" /> Own expertise
          </CardTitle>
        </CardHeader>
        <CardContent className="space-y-3">
          <p className="text-xs text-muted-foreground">
            Capabilities the unit declares for itself. Auto-seeded from
            the unit YAML on first activation; operator edits are
            authoritative from that point forward. Matches{" "}
            <code className="rounded bg-muted px-1 py-0.5 text-xs">
              spring unit expertise set
            </code>
            .
          </p>
          {ownQuery.isPending ? (
            <Skeleton className="h-20" />
          ) : ownQuery.error ? (
            <p className="rounded-md border border-destructive/50 bg-destructive/10 px-3 py-2 text-sm text-destructive">
              {ownQuery.error.message}
            </p>
          ) : (
            <ExpertiseEditor domains={domains} onSave={handleSaveOwn} />
          )}
        </CardContent>
      </Card>

      <Card>
        <CardHeader>
          <CardTitle className="flex items-center gap-2">
            <Layers className="h-4 w-4" /> Effective (aggregated) expertise
          </CardTitle>
        </CardHeader>
        <CardContent className="space-y-3">
          <p className="text-xs text-muted-foreground">
            The unit&apos;s own capabilities plus every descendant&apos;s,
            recursively composed through the member graph. Each row shows
            the originating agent or sub-unit and its depth. Matches{" "}
            <code className="rounded bg-muted px-1 py-0.5 text-xs">
              spring unit expertise aggregated
            </code>
            .
          </p>
          {aggregatedQuery.isPending ? (
            <Skeleton className="h-20" />
          ) : aggregatedQuery.error ? (
            <p className="rounded-md border border-destructive/50 bg-destructive/10 px-3 py-2 text-sm text-destructive">
              {aggregatedQuery.error.message}
            </p>
          ) : (
            <AggregatedExpertiseList
              entries={aggregated?.entries ?? []}
              computedAt={aggregated?.computedAt}
              depth={aggregated?.depth}
            />
          )}
        </CardContent>
      </Card>
    </div>
  );
}

interface AggregatedListProps {
  entries: AggregatedExpertiseEntryDto[];
  computedAt?: string | null;
  depth?: number | string | null;
}

function AggregatedExpertiseList({
  entries,
  computedAt,
  depth,
}: AggregatedListProps) {
  const sorted = useMemo(
    () =>
      [...entries].sort((a, b) => {
        const nameA = a.domain?.name ?? "";
        const nameB = b.domain?.name ?? "";
        return nameA.localeCompare(nameB);
      }),
    [entries],
  );

  if (sorted.length === 0) {
    return (
      <p className="text-sm text-muted-foreground">
        No expertise composed from this unit yet. Add members with
        expertise or declare the unit&apos;s own on the left.
      </p>
    );
  }

  return (
    <div className="space-y-3">
      <ul className="space-y-2" aria-label="Aggregated expertise entries">
        {sorted.map((e, i) => {
          const domain = e.domain;
          const origin = e.origin;
          const depthFromRoot = e.path?.length
            ? Math.max(0, e.path.length - 1)
            : 0;
          const originHref = buildEntityHref(origin);
          const originLabel = origin
            ? `${origin.scheme}://${origin.path}`
            : "(unknown)";
          return (
            <li
              key={`${domain?.name ?? "?"}-${origin?.scheme ?? ""}-${origin?.path ?? ""}-${i}`}
              className="rounded-md border border-border p-3"
            >
              <div className="flex flex-wrap items-center gap-2 text-sm">
                <span className="font-mono text-xs">
                  {domain?.name ?? "(unnamed)"}
                </span>
                {domain?.level && (
                  <Badge variant="secondary">{domain.level}</Badge>
                )}
                <Badge variant="outline">depth {depthFromRoot}</Badge>
              </div>
              <div className="mt-1 flex flex-wrap items-center gap-2 text-xs text-muted-foreground">
                <span>from</span>
                {originHref ? (
                  <Link
                    href={originHref}
                    className="font-mono text-primary hover:underline"
                  >
                    {originLabel}
                  </Link>
                ) : (
                  <span className="font-mono">{originLabel}</span>
                )}
                {domain?.description && (
                  <span>— {domain.description}</span>
                )}
              </div>
            </li>
          );
        })}
      </ul>
      {(computedAt || depth !== undefined) && (
        <p className="text-xs text-muted-foreground">
          {depth !== undefined && depth !== null && (
            <>Depth: {String(depth)} · </>
          )}
          {computedAt && (
            <>Computed: {new Date(computedAt).toLocaleString()}</>
          )}
        </p>
      )}
    </div>
  );
}

function buildEntityHref(
  address: { scheme: string; path: string } | null | undefined,
): string | null {
  if (!address) return null;
  if (address.scheme === "agent") {
    // `/agents/<id>` is retired; unit Explorer is the canonical surface.
    return `/units?node=${encodeURIComponent(address.path)}`;
  }
  if (address.scheme === "unit") {
    return `/units?node=${encodeURIComponent(address.path)}`;
  }
  return null;
}
