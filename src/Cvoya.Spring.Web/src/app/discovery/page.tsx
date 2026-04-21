"use client";

/**
 * /discovery — Tenant-wide expertise discovery (#486 / #542, renamed
 * from `/directory` by #869 per the v2 IA rework in umbrella #815).
 *
 * Calls the POST /api/v1/directory/search endpoint with the user's
 * free-text query + filters so ranking (exact slug > tag/domain > text
 * relevance > aggregated coverage) and boundary scoping happen
 * server-side. Each row deep-links to the owning agent or unit so
 * operators can jump straight to the per-entity editor.
 *
 * The CLI counterpart is `spring directory search` — both surfaces ride
 * the same endpoint per CONVENTIONS.md § ui-cli-parity. The backend
 * search endpoint keeps its `/api/v1/directory/search` path; only the
 * portal route renames.
 */

import Link from "next/link";
import { useMemo, useState } from "react";
import { Compass, GraduationCap, Search } from "lucide-react";
import { useQuery, keepPreviousData } from "@tanstack/react-query";

import { Badge } from "@/components/ui/badge";
import { Button } from "@/components/ui/button";
import {
  Card,
  CardContent,
  CardHeader,
  CardTitle,
} from "@/components/ui/card";
import { Input } from "@/components/ui/input";
import { Skeleton } from "@/components/ui/skeleton";
import { api } from "@/lib/api/client";
import type { DirectorySearchHitResponse } from "@/lib/api/types";
import { cn } from "@/lib/utils";

/**
 * Filter chip — pill-styled wrapper matching the v2 activity surface
 * (`/activity`). Active chips pick up the brand tint so the current
 * filter set is legible at a glance.
 */
function FilterChip({
  label,
  active,
  children,
}: {
  label: string;
  active: boolean;
  children: React.ReactNode;
}) {
  return (
    <label
      className={cn(
        "inline-flex min-w-0 items-center gap-2 rounded-full border px-3 py-1 text-xs transition-colors",
        active
          ? "border-primary/40 bg-primary/10 text-foreground"
          : "border-border bg-muted/40 text-muted-foreground hover:text-foreground",
      )}
    >
      <span className="shrink-0 font-medium uppercase tracking-wide text-[10px] text-muted-foreground">
        {label}
      </span>
      {children}
    </label>
  );
}

/** Level pill variant. Expert is the brand hue; mid-tiers are softer. */
const levelVariant: Record<
  string,
  "default" | "success" | "warning" | "destructive" | "secondary" | "outline"
> = {
  expert: "default",
  advanced: "secondary",
  intermediate: "outline",
  basic: "outline",
};

const PAGE_SIZE = 50;

export default function DirectoryPage() {
  const [searchInput, setSearchInput] = useState("");
  const [query, setQuery] = useState("");
  const [ownerFilter, setOwnerFilter] = useState<"" | "agent" | "unit">("");
  const [typedOnly, setTypedOnly] = useState(false);
  const [offset, setOffset] = useState(0);

  const searchQuery = useQuery({
    queryKey: [
      "directory-search",
      query,
      ownerFilter,
      typedOnly,
      offset,
    ],
    queryFn: () =>
      api.searchDirectory({
        text: query || null,
        typedOnly,
        insideUnit: false,
        limit: PAGE_SIZE,
        offset,
      }),
    placeholderData: keepPreviousData,
  });

  const allHits: DirectorySearchHitResponse[] = useMemo(
    () => searchQuery.data?.hits ?? [],
    [searchQuery.data],
  );

  // Owner scheme is a client-side narrow — the server endpoint filters
  // on an exact address, not on a scheme prefix. Rather than add a
  // dedicated server filter for the two-choice radio we post-filter here;
  // the per-entity result set is already bounded by the server's page
  // size so this is cheap.
  const filteredHits = useMemo(() => {
    if (!ownerFilter) {
      return allHits;
    }
    return allHits.filter((hit) => hit.owner?.scheme === ownerFilter);
  }, [allHits, ownerFilter]);

  // The OpenAPI generator exposes int32 fields as `number | string` to
  // accommodate JS-BigInt clients. Normalise through Number() so UI math
  // can treat them as plain numbers.
  const totalCount = Number(searchQuery.data?.totalCount ?? 0);
  const effectiveLimit = Number(searchQuery.data?.limit ?? PAGE_SIZE);
  const effectiveOffset = Number(searchQuery.data?.offset ?? 0);
  const hasMore = effectiveOffset + effectiveLimit < totalCount;

  const loadError =
    searchQuery.error instanceof Error ? searchQuery.error.message : null;

  const applySearch = () => {
    setOffset(0);
    setQuery(searchInput);
  };

  const anyFilter =
    searchInput.length > 0 || ownerFilter !== "" || typedOnly;

  return (
    <div className="space-y-6">
      <div className="flex flex-col gap-1">
        <div className="flex items-center gap-2">
          <Compass className="h-5 w-5 text-primary" aria-hidden="true" />
          <h1 className="text-2xl font-bold">Discovery</h1>
        </div>
        <p className="text-sm text-muted-foreground">
          Expertise declared by every agent and unit in the tenant. Ranked
          by relevance; outside a unit boundary only projected entries
          appear.
        </p>
      </div>

      {/* Filter bar — chip-style pills with inline controls, matching
          the `/activity` surface. The big search Input remains visible
          at the top so screen-readers (and existing tests keyed off
          `Search expertise`) still find it; the chip row below collapses
          the owner + typed-only toggles into pill controls. */}
      <Card>
        <CardContent className="space-y-3 p-4">
          <div className="flex items-center gap-2">
            <Search
              className="h-4 w-4 shrink-0 text-muted-foreground"
              aria-hidden="true"
            />
            <Input
              type="search"
              placeholder="Capability, description, domain…"
              value={searchInput}
              onChange={(e) => setSearchInput(e.target.value)}
              onKeyDown={(e) => {
                if (e.key === "Enter") {
                  applySearch();
                }
              }}
              aria-label="Search expertise"
              className="flex-1"
            />
            <Button
              onClick={applySearch}
              variant="default"
              disabled={searchQuery.isFetching}
            >
              Search
            </Button>
          </div>
          <div className="flex flex-wrap items-center gap-2">
            <FilterChip label="Owner" active={ownerFilter !== ""}>
              <select
                value={ownerFilter}
                onChange={(e) => {
                  setOffset(0);
                  setOwnerFilter(
                    e.target.value === ""
                      ? ""
                      : (e.target.value as "agent" | "unit"),
                  );
                }}
                aria-label="Owner"
                className="h-6 rounded-full border-0 bg-transparent text-xs focus-visible:outline-none"
              >
                <option value="">Any</option>
                <option value="agent">Agents</option>
                <option value="unit">Units</option>
              </select>
            </FilterChip>
            <FilterChip label="Typed" active={typedOnly}>
              <input
                type="checkbox"
                checked={typedOnly}
                onChange={(e) => {
                  setOffset(0);
                  setTypedOnly(e.target.checked);
                }}
                aria-label="Typed contract only"
                className="h-3 w-3"
              />
              <span className="text-xs text-muted-foreground">Only</span>
            </FilterChip>
            {anyFilter && (
              <button
                type="button"
                onClick={() => {
                  setSearchInput("");
                  setOwnerFilter("");
                  setTypedOnly(false);
                  setOffset(0);
                }}
                className="ml-auto text-xs text-muted-foreground hover:text-foreground"
              >
                Clear filters
              </button>
            )}
          </div>
        </CardContent>
      </Card>

      {loadError && (
        <Card>
          <CardContent className="p-6">
            <p
              className="text-sm text-destructive"
              role="alert"
              data-testid="directory-error"
            >
              Failed to load directory: {loadError}
            </p>
          </CardContent>
        </Card>
      )}

      {searchQuery.isPending ? (
        <Skeleton className="h-40" />
      ) : filteredHits.length === 0 ? (
        <Card>
          <CardHeader>
            <CardTitle>No results</CardTitle>
          </CardHeader>
          <CardContent className="text-sm text-muted-foreground">
            No expertise entries match the current filters. Declare
            capabilities with{" "}
            <code className="rounded bg-muted px-1 py-0.5 text-xs">
              spring agent expertise set
            </code>{" "}
            or{" "}
            <code className="rounded bg-muted px-1 py-0.5 text-xs">
              spring unit expertise set
            </code>
            .
          </CardContent>
        </Card>
      ) : (
        <Card>
          <CardContent className="p-0">
            <ul
              className="divide-y divide-border"
              aria-label="Expertise directory"
            >
              {filteredHits.map((hit) => (
                <DirectoryRow key={hitKey(hit)} hit={hit} />
              ))}
            </ul>
          </CardContent>
        </Card>
      )}

      <div className="flex flex-col gap-2 text-xs text-muted-foreground sm:flex-row sm:items-center sm:justify-between">
        <span>
          Showing {filteredHits.length} of {totalCount} entries
          {ownerFilter && " (owner filter applied client-side)"}
        </span>
        <div className="flex items-center gap-2">
          <Button
            variant="outline"
            size="sm"
            disabled={offset === 0 || searchQuery.isFetching}
            onClick={() => setOffset(Math.max(0, offset - PAGE_SIZE))}
          >
            Previous
          </Button>
          <Button
            variant="outline"
            size="sm"
            disabled={!hasMore || searchQuery.isFetching}
            onClick={() => setOffset(offset + PAGE_SIZE)}
          >
            Next
          </Button>
        </div>
      </div>
    </div>
  );
}

function DirectoryRow({ hit }: { hit: DirectorySearchHitResponse }) {
  const slug = hit.slug ?? "";
  const name = hit.domain?.name ?? "";
  const description = hit.domain?.description ?? "";
  const level = hit.domain?.level ?? null;
  const owner = hit.owner;
  const ownerScheme = owner?.scheme ?? "";
  const ownerPath = owner?.path ?? "";
  const href =
    ownerScheme === "agent"
      ? `/agents/${encodeURIComponent(ownerPath)}`
      : ownerScheme === "unit"
        ? `/units/${encodeURIComponent(ownerPath)}`
        : "#";

  // #553: when a hit surfaced via aggregation, render a compact
  // "Projected via" pill with the ancestor chain as a hover tooltip.
  // Keeps the main row scannable while still surfacing the lineage for
  // operators who need it. The chain is bottom-up (closest ancestor
  // first); join with " -> " so the title reads as a breadcrumb.
  const ancestorChain = hit.ancestorChain ?? [];
  const chainText = ancestorChain
    .map((addr) => `${addr.scheme}://${addr.path}`)
    .join(" -> ");

  const levelKey = (level ?? "").toLowerCase();
  const levelPillVariant = levelVariant[levelKey] ?? "secondary";

  return (
    <li
      className="flex flex-col gap-2 px-4 py-3 text-sm transition-colors hover:bg-accent/30 sm:flex-row sm:items-center sm:justify-between"
      data-testid={`directory-row-${ownerScheme}-${ownerPath}-${name}`}
    >
      <div className="min-w-0 flex-1 space-y-1">
        <div className="flex flex-wrap items-center gap-2">
          {/* Slug = identity; mono + outline badge for the v2 pattern. */}
          <code className="rounded bg-muted px-1.5 py-0.5 font-mono text-xs">
            {slug}
          </code>
          <span className="text-sm font-medium">{name}</span>
          {level && <Badge variant={levelPillVariant}>{level}</Badge>}
          {hit.typedContract && (
            <Badge variant="default" className="text-[10px] font-mono">
              typed
            </Badge>
          )}
          {ancestorChain.length > 0 && (
            <Badge
              variant="outline"
              className="text-[10px] font-normal"
              title={`Projected via: ${chainText}`}
              data-testid={`directory-row-${ownerScheme}-${ownerPath}-${name}-chain`}
            >
              Projected via {ancestorChain.length}
            </Badge>
          )}
        </div>
        {description && (
          <p className="text-xs text-muted-foreground">{description}</p>
        )}
      </div>
      <div className="flex flex-wrap items-center gap-2 text-xs text-muted-foreground sm:justify-end">
        <Badge
          variant={ownerScheme === "agent" ? "secondary" : "outline"}
          className="font-mono text-[11px]"
        >
          {ownerScheme}
        </Badge>
        <Link
          href={href}
          className="inline-flex items-center gap-1 font-mono text-primary hover:underline"
        >
          {ownerScheme}://{ownerPath}
          <GraduationCap className="h-3 w-3" aria-hidden="true" />
        </Link>
      </div>
    </li>
  );
}

function hitKey(hit: DirectorySearchHitResponse): string {
  const owner = hit.owner;
  const ownerKey = owner ? `${owner.scheme}:${owner.path}` : "";
  const agg = hit.aggregatingUnit;
  const aggKey = agg ? `${agg.scheme}:${agg.path}` : "";
  return `${hit.slug ?? ""}|${ownerKey}|${aggKey}`;
}
