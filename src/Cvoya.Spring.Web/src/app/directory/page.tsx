"use client";

/**
 * /directory — Tenant-wide expertise directory (#486 / #542).
 *
 * Calls the POST /api/v1/directory/search endpoint with the user's
 * free-text query + filters so ranking (exact slug > tag/domain > text
 * relevance > aggregated coverage) and boundary scoping happen
 * server-side. Each row deep-links to the owning agent or unit so
 * operators can jump straight to the per-entity editor.
 *
 * The CLI counterpart is `spring directory search` — both surfaces ride
 * the same endpoint per CONVENTIONS.md § ui-cli-parity.
 */

import Link from "next/link";
import { useMemo, useState } from "react";
import { GraduationCap } from "lucide-react";
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

  return (
    <div className="space-y-6">
      <div>
        <h1 className="flex items-center gap-2 text-2xl font-bold">
          <GraduationCap className="h-5 w-5" /> Directory
        </h1>
        <p className="text-sm text-muted-foreground">
          Expertise domains declared by every agent and unit in the tenant.
          Ranked by relevance; outside a unit boundary only projected
          entries appear.
        </p>
      </div>

      <Card>
        {/* Directory filters stack on mobile (each input occupies the full
            card width) and fan out to a 1fr + two 160px + button grid on
            sm+. Keeps the "one line per field" rhythm on a 375px pane. */}
        <CardContent className="grid grid-cols-1 gap-3 p-4 sm:grid-cols-[1fr_160px_160px_auto]">
          <label className="block space-y-1">
            <span className="text-xs text-muted-foreground">Search</span>
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
            />
          </label>
          <label className="block space-y-1">
            <span className="text-xs text-muted-foreground">Owner</span>
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
              className="flex h-9 w-full rounded-md border border-input bg-background px-3 text-sm shadow-sm focus-visible:outline-none focus-visible:ring-1 focus-visible:ring-ring"
            >
              <option value="">Any</option>
              <option value="agent">Agents</option>
              <option value="unit">Units</option>
            </select>
          </label>
          <label className="flex items-end gap-2 text-xs">
            <input
              type="checkbox"
              checked={typedOnly}
              onChange={(e) => {
                setOffset(0);
                setTypedOnly(e.target.checked);
              }}
              aria-label="Typed contract only"
              className="h-4 w-4"
            />
            <span className="text-muted-foreground">Typed only</span>
          </label>
          <Button
            onClick={applySearch}
            variant="default"
            className="self-end"
            disabled={searchQuery.isFetching}
          >
            Search
          </Button>
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

  return (
    <li
      className="flex flex-col gap-1 px-4 py-3 text-sm sm:flex-row sm:items-center sm:justify-between"
      data-testid={`directory-row-${ownerScheme}-${ownerPath}-${name}`}
    >
      <div className="min-w-0 flex-1 space-y-1">
        <div className="flex flex-wrap items-center gap-2">
          <code className="rounded bg-muted px-1 py-0.5 font-mono text-xs">
            {slug}
          </code>
          <span className="text-sm font-medium">{name}</span>
          {level && <Badge variant="secondary">{level}</Badge>}
          {hit.typedContract && (
            <Badge variant="default" className="text-[10px]">
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
        <Badge variant="outline">{ownerScheme}</Badge>
        <Link href={href} className="font-mono text-primary hover:underline">
          {ownerScheme}://{ownerPath}
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
