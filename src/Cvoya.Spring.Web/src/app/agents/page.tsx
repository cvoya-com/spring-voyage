"use client";

/**
 * /agents — first-class Agents lens (#450, PR-S1 Sub-PR C).
 *
 * Elevates the placeholder from PR #544 (PR-S1 Sub-PR A) into a proper
 * lens over the full tenant roster. The page rides the existing
 * `GET /api/v1/agents` endpoint (`useAgents()`) plus, when the user
 * types an expertise query, `POST /api/v1/directory/search` to filter
 * down to agents whose declared expertise matches. No new endpoints.
 *
 * Every filter in the bar maps to something an operator can do via CLI
 * or an existing portal page:
 *
 *   - Search  ↔ `spring agent list | grep` (client-side substring over
 *                 name / displayName / role).
 *   - Unit    ↔ `spring unit members list <unit>` (client-side filter
 *                 over the `parentUnit` column).
 *   - Status  ↔ the `enabled` column on `spring agent list` (client-side
 *                 narrowing on enabled / disabled).
 *   - Expertise ↔ `spring directory search <text>` (server-side ranked
 *                 search, narrowed to `owner.scheme === "agent"`).
 *
 * Filters not present here (hosting mode, initiative level) need a
 * server-side filter plus matching CLI flags first. Tracked as parity
 * follow-ups #572 (hosting mode) and #573 (initiative level). Dropping
 * them here keeps the lens's bar — every filter maps to a CLI verb —
 * intact.
 *
 * Filter state is serialised into the URL query string (`?q=…&unit=…
 * &status=…&expertise=…&group=…`) so a link captures the filtered view
 * verbatim — no saved-view store, matching the Conversations and
 * Directory lenses.
 */

import { Suspense, useMemo } from "react";
import Link from "next/link";
import { useRouter, useSearchParams } from "next/navigation";
import {
  Activity,
  GraduationCap,
  MessagesSquare,
  Network,
  Plus,
  RefreshCw,
  Users,
} from "lucide-react";
import { keepPreviousData, useQuery } from "@tanstack/react-query";

import { AgentCard } from "@/components/cards/agent-card";
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
import { useAgents } from "@/lib/api/queries";
import type { AgentResponse } from "@/lib/api/types";

type StatusFilter = "" | "enabled" | "disabled";
type Grouping = "flat" | "unit";

const STATUSES: Array<{ value: StatusFilter; label: string }> = [
  { value: "", label: "Any" },
  { value: "enabled", label: "Enabled" },
  { value: "disabled", label: "Disabled" },
];

const GROUPINGS: Array<{ value: Grouping; label: string }> = [
  { value: "flat", label: "Flat" },
  { value: "unit", label: "By unit" },
];

/** Sentinel for the "(no unit)" bucket when grouping by unit. */
const UNASSIGNED = "__unassigned__";

function setOrDelete(
  params: URLSearchParams,
  key: string,
  value: string | null,
) {
  if (value === null || value === "") {
    params.delete(key);
  } else {
    params.set(key, value);
  }
}

function AgentsLensContent() {
  const router = useRouter();
  const searchParams = useSearchParams();

  const q = searchParams.get("q") ?? "";
  const unitFilter = searchParams.get("unit") ?? "";
  const rawStatus = searchParams.get("status");
  const statusFilter: StatusFilter =
    rawStatus === "enabled" || rawStatus === "disabled" ? rawStatus : "";
  const expertise = searchParams.get("expertise") ?? "";
  const rawGroup = searchParams.get("group");
  const grouping: Grouping = rawGroup === "unit" ? "unit" : "flat";

  const updateParam = (key: string, value: string) => {
    const params = new URLSearchParams(searchParams.toString());
    setOrDelete(params, key, value || null);
    const qs = params.toString();
    router.replace(qs ? `/agents?${qs}` : "/agents");
  };

  const agentsQuery = useAgents();
  const agents: AgentResponse[] = useMemo(
    () => agentsQuery.data ?? [],
    [agentsQuery.data],
  );

  // Expertise narrow — POST to the directory search endpoint with the
  // user's free-text query, then keep the subset of agent owners whose
  // names match an entry in `agents`. The search runs server-side so
  // the CLI parity (`spring directory search <text>`) round-trips the
  // exact same ranking model. `keepPreviousData` avoids a flash when
  // the user types another character.
  const expertiseSearch = useQuery({
    queryKey: ["agents-lens", "expertise", expertise],
    queryFn: () =>
      api.searchDirectory({
        text: expertise,
        typedOnly: false,
        insideUnit: false,
        limit: 200,
        offset: 0,
      }),
    enabled: expertise.trim().length > 0,
    placeholderData: keepPreviousData,
  });

  // Set of agent names returned by the expertise search. We match on
  // `owner.scheme === "agent"` and use `owner.path` as the agent name,
  // which is what `AgentResponse.name` carries.
  const expertiseAgentNames = useMemo(() => {
    if (!expertise.trim()) return null;
    const hits = expertiseSearch.data?.hits ?? [];
    const names = new Set<string>();
    for (const hit of hits) {
      if (hit.owner?.scheme === "agent" && hit.owner.path) {
        names.add(hit.owner.path);
      }
    }
    return names;
  }, [expertise, expertiseSearch.data]);

  const filteredAgents = useMemo(() => {
    const needle = q.trim().toLowerCase();
    const unitNeedle = unitFilter.trim().toLowerCase();
    return agents.filter((a) => {
      if (needle) {
        const hay = `${a.name ?? ""} ${a.displayName ?? ""} ${a.role ?? ""}`
          .toLowerCase();
        if (!hay.includes(needle)) return false;
      }
      if (unitNeedle) {
        const parent = (a.parentUnit ?? "").toLowerCase();
        if (!parent.includes(unitNeedle)) return false;
      }
      if (statusFilter === "enabled" && !a.enabled) return false;
      if (statusFilter === "disabled" && a.enabled) return false;
      if (expertiseAgentNames && !expertiseAgentNames.has(a.name)) {
        return false;
      }
      return true;
    });
  }, [agents, q, unitFilter, statusFilter, expertiseAgentNames]);

  const errorMessage =
    agentsQuery.error instanceof Error ? agentsQuery.error.message : null;

  // Group once — the bucketing shape is the same regardless of display.
  // When `grouping === "flat"` we collapse into a single pseudo-bucket.
  const bucketed = useMemo(() => {
    if (grouping === "flat") {
      return [{ key: "all", label: null, items: filteredAgents }];
    }
    const byUnit = new Map<string, AgentResponse[]>();
    for (const a of filteredAgents) {
      const parent = a.parentUnit ?? UNASSIGNED;
      const bucket = byUnit.get(parent);
      if (bucket) {
        bucket.push(a);
      } else {
        byUnit.set(parent, [a]);
      }
    }
    // Sort: named units alphabetically, "(no unit)" pinned to the end.
    const keys = Array.from(byUnit.keys()).sort((a, b) => {
      if (a === UNASSIGNED) return 1;
      if (b === UNASSIGNED) return -1;
      return a.localeCompare(b);
    });
    return keys.map((key) => ({
      key,
      label: key === UNASSIGNED ? "No unit" : key,
      items: byUnit.get(key) ?? [],
    }));
  }, [filteredAgents, grouping]);

  const totalShown = filteredAgents.length;
  const expertiseLoading =
    expertise.trim().length > 0 && expertiseSearch.isPending;

  return (
    <div className="space-y-6">
      <div className="flex items-center justify-between gap-2">
        <div>
          <h1 className="flex items-center gap-2 text-2xl font-bold">
            <Users className="h-5 w-5" /> Agents
          </h1>
          <p className="text-sm text-muted-foreground">
            Every agent across every unit. Mirrors{" "}
            <code className="rounded bg-muted px-1 py-0.5 text-xs">
              spring agent list
            </code>
            .
          </p>
        </div>
        <Button
          variant="outline"
          size="sm"
          onClick={() => agentsQuery.refetch()}
          disabled={agentsQuery.isFetching}
          data-testid="agents-refresh"
        >
          <RefreshCw
            className={`h-4 w-4 mr-1 ${
              agentsQuery.isFetching ? "animate-spin" : ""
            }`}
          />
          Refresh
        </Button>
      </div>

      {/* Filter bar */}
      <Card>
        <CardContent className="p-4">
          <div className="flex flex-wrap gap-3">
            <label className="space-y-1">
              <span className="text-xs text-muted-foreground">Search</span>
              <Input
                type="search"
                placeholder="name, display name, or role"
                defaultValue={q}
                onBlur={(e) => updateParam("q", e.target.value.trim())}
                onKeyDown={(e) => {
                  if (e.key === "Enter") {
                    e.preventDefault();
                    updateParam("q", e.currentTarget.value.trim());
                  }
                }}
                className="w-56"
                aria-label="Search agents"
                data-testid="agents-filter-q"
              />
            </label>
            <label className="space-y-1">
              <span className="text-xs text-muted-foreground">Unit</span>
              <Input
                placeholder="e.g. engineering"
                defaultValue={unitFilter}
                onBlur={(e) => updateParam("unit", e.target.value.trim())}
                onKeyDown={(e) => {
                  if (e.key === "Enter") {
                    e.preventDefault();
                    updateParam("unit", e.currentTarget.value.trim());
                  }
                }}
                className="w-44"
                aria-label="Filter by owning unit"
                data-testid="agents-filter-unit"
              />
            </label>
            <label className="space-y-1">
              <span className="text-xs text-muted-foreground">Status</span>
              <select
                value={statusFilter}
                onChange={(e) => updateParam("status", e.target.value)}
                className="flex h-9 w-36 rounded-md border border-input bg-background px-3 py-1 text-sm shadow-sm focus-visible:outline-none focus-visible:ring-1 focus-visible:ring-ring"
                aria-label="Filter by status"
                data-testid="agents-filter-status"
              >
                {STATUSES.map((s) => (
                  <option key={s.value} value={s.value}>
                    {s.label}
                  </option>
                ))}
              </select>
            </label>
            <label className="space-y-1">
              <span className="flex items-center gap-1 text-xs text-muted-foreground">
                <GraduationCap className="h-3 w-3" />
                Expertise
              </span>
              <Input
                type="search"
                placeholder="capability or domain"
                defaultValue={expertise}
                onBlur={(e) => updateParam("expertise", e.target.value.trim())}
                onKeyDown={(e) => {
                  if (e.key === "Enter") {
                    e.preventDefault();
                    updateParam(
                      "expertise",
                      e.currentTarget.value.trim(),
                    );
                  }
                }}
                className="w-56"
                aria-label="Filter by expertise"
                data-testid="agents-filter-expertise"
              />
            </label>
            <label className="space-y-1">
              <span className="text-xs text-muted-foreground">Group by</span>
              <select
                value={grouping}
                onChange={(e) => updateParam("group", e.target.value)}
                className="flex h-9 w-36 rounded-md border border-input bg-background px-3 py-1 text-sm shadow-sm focus-visible:outline-none focus-visible:ring-1 focus-visible:ring-ring"
                aria-label="Grouping"
                data-testid="agents-filter-group"
              >
                {GROUPINGS.map((g) => (
                  <option key={g.value} value={g.value}>
                    {g.label}
                  </option>
                ))}
              </select>
            </label>
          </div>
          {expertise && (
            <p
              className="mt-3 text-xs text-muted-foreground"
              data-testid="agents-expertise-hint"
            >
              {expertiseLoading
                ? "Searching the expertise directory…"
                : `Expertise filter applied · matches ${
                    expertiseAgentNames?.size ?? 0
                  } agent(s) in the directory.`}
            </p>
          )}
        </CardContent>
      </Card>

      {/* Results */}
      <Card>
        <CardHeader>
          <CardTitle className="flex items-center gap-2">
            <Users className="h-4 w-4" />
            {grouping === "unit" ? "By unit" : "All agents"}
            {!agentsQuery.isPending && (
              <span className="ml-auto text-sm font-normal text-muted-foreground">
                {totalShown} shown · {agents.length} total
              </span>
            )}
          </CardTitle>
        </CardHeader>
        <CardContent className="space-y-6">
          {errorMessage && (
            <p
              className="rounded-md border border-destructive/50 bg-destructive/10 px-3 py-2 text-sm text-destructive"
              role="alert"
              data-testid="agents-error"
            >
              {errorMessage}
            </p>
          )}
          {agentsQuery.isPending ? (
            <div className="grid grid-cols-1 gap-3 sm:grid-cols-2 lg:grid-cols-3">
              <Skeleton className="h-32" />
              <Skeleton className="h-32" />
              <Skeleton className="h-32" />
            </div>
          ) : totalShown === 0 ? (
            <EmptyState hasAnyAgents={agents.length > 0} />
          ) : (
            bucketed.map((bucket) => (
              <section
                key={bucket.key}
                className="space-y-3"
                data-testid={`agents-bucket-${bucket.key}`}
              >
                {bucket.label !== null && (
                  <div className="flex items-center justify-between">
                    <h2 className="flex items-center gap-2 text-sm font-semibold">
                      <Network className="h-4 w-4 text-muted-foreground" />
                      {bucket.key === UNASSIGNED ? (
                        <span className="text-muted-foreground">
                          {bucket.label}
                        </span>
                      ) : (
                        <Link
                          href={`/units/${encodeURIComponent(bucket.key)}`}
                          className="hover:underline"
                        >
                          {bucket.label}
                        </Link>
                      )}
                      <Badge variant="outline" className="text-[10px]">
                        {bucket.items.length}
                      </Badge>
                    </h2>
                  </div>
                )}
                <div className="grid grid-cols-1 gap-3 sm:grid-cols-2 lg:grid-cols-3">
                  {bucket.items.map((a) => (
                    <AgentCard
                      key={a.id || a.name}
                      agent={{
                        name: a.name,
                        displayName: a.displayName,
                        role: a.role,
                        registeredAt: a.registeredAt,
                        parentUnit: a.parentUnit,
                        executionMode: a.executionMode,
                        status: a.enabled ? "active" : "idle",
                      }}
                      actions={<LensQuickActions agent={a} />}
                    />
                  ))}
                </div>
              </section>
            ))
          )}
        </CardContent>
      </Card>
    </div>
  );
}

/**
 * Lens-specific quick actions slotted into each `<AgentCard>` via the
 * shared `actions` prop (DESIGN.md § 7.11). These are cross-links to
 * surfaces that already exist:
 *
 *   - Conversation ↔ `/conversations?participant=agent://<name>`
 *     (mirrors `spring conversation list --participant agent://<name>`).
 *   - Deployment   ↔ `/agents/<name>#deployment` anchor (mirrors
 *     `spring agent deploy|scale|undeploy|logs`). Rendered for every
 *     agent — the lifecycle panel itself surfaces the server's 400 for
 *     ephemeral agents, matching the CLI.
 */
function LensQuickActions({ agent }: { agent: AgentResponse }) {
  const encoded = encodeURIComponent(agent.name);
  return (
    <>
      <Link
        href={`/conversations?participant=${encodeURIComponent(`agent://${agent.name}`)}`}
        aria-label={`Open conversations for ${agent.displayName}`}
        title="Conversations involving this agent"
        data-testid={`agent-lens-conversation-${agent.name}`}
        className="inline-flex h-7 items-center gap-1 rounded-md px-2 text-xs text-muted-foreground transition-colors hover:bg-accent hover:text-foreground"
      >
        <MessagesSquare className="h-3.5 w-3.5" />
        Conversation
      </Link>
      <Link
        href={`/agents/${encoded}#deployment`}
        aria-label={`Open deployment panel for ${agent.displayName}`}
        title="Jump to the persistent deployment panel"
        data-testid={`agent-lens-deployment-${agent.name}`}
        className="inline-flex h-7 items-center gap-1 rounded-md px-2 text-xs text-muted-foreground transition-colors hover:bg-accent hover:text-foreground"
      >
        <Activity className="h-3.5 w-3.5" />
        Deployment
      </Link>
    </>
  );
}

function EmptyState({ hasAnyAgents }: { hasAnyAgents: boolean }) {
  // Two different empty states per DESIGN.md § 7.3: a search-empty
  // variant that nudges the user to widen the filters, and a fleet-empty
  // variant that points at the surfaces that create agents.
  if (hasAnyAgents) {
    return (
      <div
        className="py-6 text-center text-sm text-muted-foreground"
        data-testid="agents-search-empty"
      >
        <p>No agents match these filters.</p>
        <p className="mt-2">
          Widen the filters above, or browse the{" "}
          <Link href="/directory" className="text-primary hover:underline">
            expertise directory
          </Link>{" "}
          for capabilities.
        </p>
      </div>
    );
  }
  return (
    <div
      className="py-8 text-center"
      data-testid="agents-empty"
    >
      <Users className="mx-auto h-10 w-10 text-muted-foreground" />
      <p className="mt-3 text-sm font-medium">No agents yet</p>
      <p className="mx-auto mt-1 max-w-md text-xs text-muted-foreground">
        Agents appear when you add them to a unit. Start from the units
        list, or browse installed packages for agent templates.
      </p>
      <div className="mt-4 flex flex-wrap items-center justify-center gap-2">
        <Link
          href="/units"
          className="inline-flex h-8 items-center rounded-md bg-primary px-3 text-xs font-medium text-primary-foreground transition-colors hover:bg-primary/90"
        >
          <Network className="mr-1 h-4 w-4" />
          Browse units
        </Link>
        <Link
          href="/directory"
          className="inline-flex h-8 items-center rounded-md border border-input bg-background px-3 text-xs font-medium transition-colors hover:bg-accent hover:text-accent-foreground"
        >
          <GraduationCap className="mr-1 h-4 w-4" />
          Expertise directory
        </Link>
        <Link
          href="/packages"
          className="inline-flex h-8 items-center rounded-md border border-input bg-background px-3 text-xs font-medium transition-colors hover:bg-accent hover:text-accent-foreground"
        >
          <Plus className="mr-1 h-4 w-4" />
          Packages
        </Link>
      </div>
      <p className="mt-4 font-mono text-[11px] text-muted-foreground">
        spring agent list · spring unit members add
      </p>
    </div>
  );
}

export default function AgentsIndexPage() {
  return (
    <Suspense fallback={<Skeleton className="h-40" />}>
      <AgentsLensContent />
    </Suspense>
  );
}
