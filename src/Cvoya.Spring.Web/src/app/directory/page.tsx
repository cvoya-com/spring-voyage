"use client";

/**
 * /directory — Tenant-wide expertise directory (#486).
 *
 * Fans out per-agent `GET /api/v1/agents/{id}/expertise` and per-unit
 * `GET /api/v1/units/{id}/expertise/own` reads, then flattens the union
 * into a searchable table. The aggregated (recursive) view is available
 * on each unit's detail page — this tenant-wide surface is the flat
 * "who declares what" index, the portal's counterpart to what a future
 * `spring directory expertise` CLI would produce (CLI parity follow-up
 * #528).
 *
 * Every row deep-links back to the owning agent or unit so operators
 * can jump into the per-entity editor.
 */

import Link from "next/link";
import { useMemo, useState } from "react";
import { GraduationCap } from "lucide-react";
import { useQueries } from "@tanstack/react-query";

import { Badge } from "@/components/ui/badge";
import {
  Card,
  CardContent,
  CardHeader,
  CardTitle,
} from "@/components/ui/card";
import { Input } from "@/components/ui/input";
import { Skeleton } from "@/components/ui/skeleton";
import { api } from "@/lib/api/client";
import { useAgents, useUnits } from "@/lib/api/queries";
import { queryKeys } from "@/lib/api/query-keys";
import { EXPERTISE_LEVELS, type ExpertiseLevel } from "@/lib/api/types";

interface DirectoryRow {
  key: string;
  ownerScheme: "agent" | "unit";
  ownerId: string;
  ownerDisplayName: string;
  domain: string;
  level: string | null;
  description: string;
}

export default function DirectoryPage() {
  const agentsQuery = useAgents();
  const unitsQuery = useUnits();

  const agents = useMemo(
    () => agentsQuery.data ?? [],
    [agentsQuery.data],
  );
  const units = useMemo(() => unitsQuery.data ?? [], [unitsQuery.data]);

  // Fan out to each agent's expertise endpoint. React Query dedupes + caches
  // each fetch by key so toggling between list and detail pages doesn't
  // re-hit the server when a per-entity panel has already warmed the cache.
  const agentExpertiseQueries = useQueries({
    queries: agents.map((agent) => ({
      queryKey: queryKeys.agents.expertise(agent.name),
      queryFn: () => api.getAgentExpertise(agent.name),
      enabled: agentsQuery.isSuccess,
    })),
  });

  const unitOwnExpertiseQueries = useQueries({
    queries: units.map((unit) => ({
      queryKey: queryKeys.units.ownExpertise(unit.name),
      queryFn: () => api.getUnitOwnExpertise(unit.name),
      enabled: unitsQuery.isSuccess,
    })),
  });

  const rows = useMemo<DirectoryRow[]>(() => {
    const out: DirectoryRow[] = [];

    agents.forEach((agent, i) => {
      const domains = agentExpertiseQueries[i]?.data ?? [];
      for (const d of domains) {
        out.push({
          key: `agent:${agent.name}:${d.name}`,
          ownerScheme: "agent",
          ownerId: agent.name,
          ownerDisplayName: agent.displayName || agent.name,
          domain: d.name ?? "",
          level: d.level ?? null,
          description: d.description ?? "",
        });
      }
    });

    units.forEach((unit, i) => {
      const domains = unitOwnExpertiseQueries[i]?.data ?? [];
      for (const d of domains) {
        out.push({
          key: `unit:${unit.name}:${d.name}`,
          ownerScheme: "unit",
          ownerId: unit.name,
          ownerDisplayName: unit.displayName || unit.name,
          domain: d.name ?? "",
          level: d.level ?? null,
          description: d.description ?? "",
        });
      }
    });

    return out.sort(
      (a, b) =>
        a.domain.localeCompare(b.domain) ||
        a.ownerDisplayName.localeCompare(b.ownerDisplayName),
    );
  }, [agents, units, agentExpertiseQueries, unitOwnExpertiseQueries]);

  const [search, setSearch] = useState("");
  const [levelFilter, setLevelFilter] = useState<"" | ExpertiseLevel>("");
  const [ownerFilter, setOwnerFilter] = useState<"" | "agent" | "unit">("");

  const filtered = useMemo(() => {
    const needle = search.trim().toLowerCase();
    return rows.filter((row) => {
      if (ownerFilter && row.ownerScheme !== ownerFilter) return false;
      if (levelFilter && row.level !== levelFilter) return false;
      if (needle) {
        const hay =
          row.domain.toLowerCase() +
          " " +
          row.description.toLowerCase() +
          " " +
          row.ownerDisplayName.toLowerCase() +
          " " +
          row.ownerId.toLowerCase();
        if (!hay.includes(needle)) return false;
      }
      return true;
    });
  }, [rows, search, levelFilter, ownerFilter]);

  const baseLoading = agentsQuery.isPending || unitsQuery.isPending;
  const fanoutPending =
    agentExpertiseQueries.some((q) => q.isPending) ||
    unitOwnExpertiseQueries.some((q) => q.isPending);
  const loading = baseLoading || fanoutPending;

  const loadError =
    (agentsQuery.error instanceof Error
      ? agentsQuery.error.message
      : null) ??
    (unitsQuery.error instanceof Error ? unitsQuery.error.message : null);

  return (
    <div className="space-y-6">
      <div>
        <h1 className="flex items-center gap-2 text-2xl font-bold">
          <GraduationCap className="h-5 w-5" /> Directory
        </h1>
        <p className="text-sm text-muted-foreground">
          Expertise domains declared by every agent and unit in the tenant.
          Auto-seeded from YAML definitions; operator edits on the agent
          or unit detail page are authoritative from that point forward.
        </p>
      </div>

      <Card>
        <CardContent className="grid grid-cols-1 gap-3 p-4 sm:grid-cols-[1fr_160px_160px]">
          <label className="block space-y-1">
            <span className="text-xs text-muted-foreground">Search</span>
            <Input
              type="search"
              placeholder="Domain, description, owner…"
              value={search}
              onChange={(e) => setSearch(e.target.value)}
              aria-label="Search expertise"
            />
          </label>
          <label className="block space-y-1">
            <span className="text-xs text-muted-foreground">Level</span>
            <select
              value={levelFilter}
              onChange={(e) =>
                setLevelFilter(
                  e.target.value === ""
                    ? ""
                    : (e.target.value as ExpertiseLevel),
                )
              }
              className="flex h-9 w-full rounded-md border border-input bg-background px-3 text-sm shadow-sm focus-visible:outline-none focus-visible:ring-1 focus-visible:ring-ring"
            >
              <option value="">Any</option>
              {EXPERTISE_LEVELS.map((lvl) => (
                <option key={lvl} value={lvl}>
                  {lvl}
                </option>
              ))}
            </select>
          </label>
          <label className="block space-y-1">
            <span className="text-xs text-muted-foreground">Owner</span>
            <select
              value={ownerFilter}
              onChange={(e) =>
                setOwnerFilter(
                  e.target.value === ""
                    ? ""
                    : (e.target.value as "agent" | "unit"),
                )
              }
              className="flex h-9 w-full rounded-md border border-input bg-background px-3 text-sm shadow-sm focus-visible:outline-none focus-visible:ring-1 focus-visible:ring-ring"
            >
              <option value="">Any</option>
              <option value="agent">Agents</option>
              <option value="unit">Units</option>
            </select>
          </label>
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

      {baseLoading ? (
        <Skeleton className="h-40" />
      ) : rows.length === 0 ? (
        <Card>
          <CardHeader>
            <CardTitle>No expertise declared</CardTitle>
          </CardHeader>
          <CardContent className="text-sm text-muted-foreground">
            Declare capabilities with{" "}
            <code className="rounded bg-muted px-1 py-0.5 text-xs">
              spring agent expertise set
            </code>{" "}
            or{" "}
            <code className="rounded bg-muted px-1 py-0.5 text-xs">
              spring unit expertise set
            </code>{" "}
            — or seed them via an <code>expertise:</code> block in the
            agent/unit YAML.
          </CardContent>
        </Card>
      ) : filtered.length === 0 ? (
        <Card>
          <CardContent className="p-6 text-sm text-muted-foreground">
            No domains match the current filters.
          </CardContent>
        </Card>
      ) : (
        <Card>
          <CardContent className="p-0">
            <ul
              className="divide-y divide-border"
              aria-label="Expertise directory"
            >
              {filtered.map((row) => (
                <li
                  key={row.key}
                  className="flex flex-col gap-1 px-4 py-3 text-sm sm:flex-row sm:items-center sm:justify-between"
                  data-testid={`directory-row-${row.ownerScheme}-${row.ownerId}-${row.domain}`}
                >
                  <div className="min-w-0 flex-1 space-y-1">
                    <div className="flex flex-wrap items-center gap-2">
                      <span className="font-mono text-xs">{row.domain}</span>
                      {row.level && (
                        <Badge variant="secondary">{row.level}</Badge>
                      )}
                    </div>
                    {row.description && (
                      <p className="text-xs text-muted-foreground">
                        {row.description}
                      </p>
                    )}
                  </div>
                  <div className="flex flex-wrap items-center gap-2 text-xs text-muted-foreground sm:justify-end">
                    <Badge variant="outline">{row.ownerScheme}</Badge>
                    <Link
                      href={
                        row.ownerScheme === "agent"
                          ? `/agents/${encodeURIComponent(row.ownerId)}`
                          : `/units/${encodeURIComponent(row.ownerId)}`
                      }
                      className="font-mono text-primary hover:underline"
                    >
                      {row.ownerScheme}://{row.ownerId}
                    </Link>
                  </div>
                </li>
              ))}
            </ul>
          </CardContent>
        </Card>
      )}

      {loading && !baseLoading && (
        <p className="text-xs text-muted-foreground">
          Loading expertise entries…
        </p>
      )}

      <p className="text-xs text-muted-foreground">
        Showing {filtered.length} of {rows.length} entries. The aggregated
        (recursive) view is available on each unit&apos;s detail page.
      </p>
    </div>
  );
}
