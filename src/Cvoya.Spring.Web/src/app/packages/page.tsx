"use client";

/**
 * /packages — installed-package browser (#395 / PR-PLAT-PKG-1).
 *
 * Mirrors `spring package list` verbatim: same endpoint, same shape,
 * same sort order. Cards surface the per-content counts so a user can
 * pick "engineering-team is inside software-engineering" at a glance
 * without drilling into the detail page.
 *
 * Design contract: docs/design/portal-exploration.md § 3.2 lists
 * Packages as a primary nav entry (sidebar link wired in
 * `lib/extensions/defaults.ts`), § 5.1 describes the card-grid layout
 * that matches the Stitch/DESIGN.md primitives used by AgentCard and
 * UnitCard.
 */

import Link from "next/link";
import { Package as PackageIcon } from "lucide-react";

import { Badge } from "@/components/ui/badge";
import { Card, CardContent } from "@/components/ui/card";
import { Skeleton } from "@/components/ui/skeleton";
import { usePackages } from "@/lib/api/queries";
import type { PackageSummary } from "@/lib/api/types";
import { cn } from "@/lib/utils";

export default function PackagesListPage() {
  const packagesQuery = usePackages();
  const packages = packagesQuery.data ?? [];
  const loading = packagesQuery.isPending;

  return (
    <div className="space-y-6">
      <div>
        <h1 className="flex items-center gap-2 text-2xl font-bold">
          <PackageIcon className="h-5 w-5" /> Packages
        </h1>
        <p className="text-sm text-muted-foreground">
          Installed domain packages and the templates, skills, and
          connectors they contribute. Matches{" "}
          <code className="rounded bg-muted px-1 py-0.5 text-xs">
            spring package list
          </code>
          .
        </p>
      </div>

      {loading ? (
        <div className="grid grid-cols-1 gap-3 sm:grid-cols-2 lg:grid-cols-3">
          <Skeleton className="h-40" />
          <Skeleton className="h-40" />
          <Skeleton className="h-40" />
        </div>
      ) : packagesQuery.error ? (
        <Card>
          <CardContent className="p-6">
            <p className="text-sm text-destructive" role="alert">
              Failed to load packages: {packagesQuery.error.message}
            </p>
          </CardContent>
        </Card>
      ) : packages.length === 0 ? (
        <Card>
          <CardContent className="space-y-1 p-6">
            <p className="text-sm text-muted-foreground">
              No packages installed. The server&apos;s{" "}
              <code className="rounded bg-muted px-1 py-0.5 text-xs">
                Packages:Root
              </code>{" "}
              setting controls where the catalog reads from; when it
              isn&apos;t pointed at a valid directory the list stays
              empty.
            </p>
          </CardContent>
        </Card>
      ) : (
        <div className="grid grid-cols-1 gap-3 sm:grid-cols-2 lg:grid-cols-3">
          {packages.map((p) => (
            <PackageCard key={p.name} pkg={p} />
          ))}
        </div>
      )}
    </div>
  );
}

function PackageCard({ pkg }: { pkg: PackageSummary }) {
  const href = `/packages/${encodeURIComponent(pkg.name ?? "")}`;
  const name = pkg.name ?? "(unnamed package)";
  return (
    <Card
      data-testid={`package-card-${pkg.name}`}
      className={cn(
        "h-full transition-colors hover:border-primary/50 hover:bg-muted/30",
      )}
    >
      <CardContent className="p-4">
        <Link
          href={href}
          aria-label={`Open package ${name}`}
          className="flex items-start justify-between gap-2"
        >
          <div className="min-w-0 flex-1">
            <h3 className="truncate font-semibold">{name}</h3>
            {pkg.description && (
              <p className="mt-1 line-clamp-2 text-xs text-muted-foreground">
                {pkg.description}
              </p>
            )}
          </div>
        </Link>

        <div className="mt-3 flex flex-wrap items-center gap-1.5 text-xs">
          <CountBadge label="units" value={pkg.unitTemplateCount} />
          <CountBadge label="agents" value={pkg.agentTemplateCount} />
          <CountBadge label="skills" value={pkg.skillCount} />
          <CountBadge label="connectors" value={pkg.connectorCount} />
          <CountBadge label="workflows" value={pkg.workflowCount} />
        </div>
      </CardContent>
    </Card>
  );
}

/**
 * Counts are int-shaped on the wire, but openapi-typescript surfaces
 * them as `number | string | null | undefined` because the generator
 * widens every `format: int32` field to accommodate extreme-precision
 * string serialisation (see openapi.json schema transformer in
 * Program.cs). Normalise to a number here so the badge still renders a
 * count even when the wire payload came back as the string variant;
 * treat missing values as `0` so a single malformed row doesn't hide
 * the whole card.
 */
function CountBadge({
  label,
  value,
}: {
  label: string;
  value: number | string | null | undefined;
}) {
  const parsed =
    typeof value === "number"
      ? value
      : typeof value === "string"
        ? Number.parseInt(value, 10)
        : 0;
  const count = Number.isFinite(parsed) ? parsed : 0;
  return (
    <Badge
      variant={count === 0 ? "outline" : "secondary"}
      data-testid={`package-count-${label}`}
    >
      {count} {label}
    </Badge>
  );
}
