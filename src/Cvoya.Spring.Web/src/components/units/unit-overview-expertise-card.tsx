"use client";

/**
 * Read-only "Expertise" card for the Unit Overview tab (#936 /
 * #815 §4).
 *
 * Pulls both `useUnitOwnExpertise` (own declarations) and
 * `useUnitAggregatedExpertise` (rolled-up subtree view) and renders
 * two compact chip rows. The "Manage" link jumps to the Unit Config →
 * Expertise sub-tab where the full editor lives (user's explicit
 * choice of placement).
 */

import Link from "next/link";
import { GraduationCap, Layers } from "lucide-react";

import { Badge } from "@/components/ui/badge";
import {
  Card,
  CardContent,
  CardHeader,
  CardTitle,
} from "@/components/ui/card";
import { Skeleton } from "@/components/ui/skeleton";
import {
  useUnitAggregatedExpertise,
  useUnitOwnExpertise,
} from "@/lib/api/queries";
import type {
  AggregatedExpertiseEntryDto,
  ExpertiseDomainDto,
} from "@/lib/api/types";

interface UnitOverviewExpertiseCardProps {
  unitId: string;
}

export function UnitOverviewExpertiseCard({
  unitId,
}: UnitOverviewExpertiseCardProps) {
  const ownQuery = useUnitOwnExpertise(unitId);
  const aggregatedQuery = useUnitAggregatedExpertise(unitId);

  const own = ownQuery.data ?? [];
  const aggregated = aggregatedQuery.data?.entries ?? [];
  const manageHref = `/units?node=${encodeURIComponent(unitId)}&tab=Config&subtab=Expertise`;

  const loading = ownQuery.isPending || aggregatedQuery.isPending;
  const empty = own.length === 0 && aggregated.length === 0;

  return (
    <Card data-testid="unit-overview-expertise-card">
      <CardHeader className="flex flex-row items-center justify-between gap-2 space-y-0 pb-2">
        <CardTitle className="flex items-center gap-2 text-base">
          <GraduationCap className="h-4 w-4" aria-hidden="true" />
          <span>Expertise</span>
        </CardTitle>
        <Link
          href={manageHref}
          data-testid="unit-overview-expertise-manage"
          className="inline-flex h-8 items-center rounded-md border border-input bg-background px-3 text-xs font-medium shadow-sm hover:bg-accent hover:text-accent-foreground"
        >
          Manage
        </Link>
      </CardHeader>
      <CardContent className="space-y-3 text-sm">
        {loading ? (
          <Skeleton className="h-16" />
        ) : empty ? (
          <p className="text-xs text-muted-foreground">
            No expertise declared on this unit or its subtree yet. Use
            Manage to add some.
          </p>
        ) : (
          <>
            <ChipRow
              icon={<GraduationCap className="h-3.5 w-3.5" aria-hidden="true" />}
              label="Own"
              domains={own}
              emptyNote="(none)"
            />
            <AggregatedChipRow
              entries={aggregated}
              ownCount={own.length}
            />
          </>
        )}
      </CardContent>
    </Card>
  );
}

function ChipRow({
  icon,
  label,
  domains,
  emptyNote,
}: {
  icon: React.ReactNode;
  label: string;
  domains: readonly ExpertiseDomainDto[];
  emptyNote: string;
}) {
  return (
    <div className="space-y-1">
      <div className="flex items-center gap-1.5 text-xs text-muted-foreground">
        {icon}
        <span>{label}</span>
      </div>
      <div className="flex flex-wrap gap-1">
        {domains.length === 0 ? (
          <span className="text-xs text-muted-foreground">{emptyNote}</span>
        ) : (
          domains.map((d, i) => (
            <Badge
              key={`${d.name}-${i}`}
              variant="outline"
              className="font-mono text-xs"
            >
              {d.name}
              {d.level ? ` · ${d.level}` : ""}
            </Badge>
          ))
        )}
      </div>
    </div>
  );
}

/**
 * Chip row for the aggregated subtree view. Dedupes on domain name so
 * the Overview card doesn't get noisy when several descendants
 * declare the same capability — the full origin list stays available
 * under the Config → Expertise editor.
 */
function AggregatedChipRow({
  entries,
  ownCount,
}: {
  entries: readonly AggregatedExpertiseEntryDto[];
  ownCount: number;
}) {
  const seen = new Set<string>();
  const unique: { name: string; level: string | null | undefined }[] = [];
  for (const e of entries) {
    const name = e.domain?.name;
    if (!name || seen.has(name)) continue;
    seen.add(name);
    unique.push({ name, level: e.domain?.level ?? null });
  }
  return (
    <div className="space-y-1">
      <div className="flex items-center gap-1.5 text-xs text-muted-foreground">
        <Layers className="h-3.5 w-3.5" aria-hidden="true" />
        <span>Subtree ({unique.length} unique)</span>
      </div>
      <div className="flex flex-wrap gap-1">
        {unique.length === 0 ? (
          <span className="text-xs text-muted-foreground">
            {ownCount === 0 ? "(none)" : "(matches Own)"}
          </span>
        ) : (
          unique.map((d) => (
            <Badge key={d.name} variant="secondary" className="font-mono text-xs">
              {d.name}
              {d.level ? ` · ${d.level}` : ""}
            </Badge>
          ))
        )}
      </div>
    </div>
  );
}
