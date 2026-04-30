"use client";

// Agents lens — tenant-wide agent list with Hosting and Initiative
// filters (#1403). Client-side filtering against the full list returned
// by GET /api/v1/tenant/agents. Server-side filtering via ?hosting= /
// ?initiative= query params is tracked as follow-up #1402.
//
// FilterChip pattern from `/activity`; AgentCard from
// `src/components/cards/agent-card.tsx`.

import { useMemo, useState } from "react";
import Link from "next/link";
import { Bot } from "lucide-react";
import { useQuery } from "@tanstack/react-query";

import { AgentCard, type AgentCardAgent } from "@/components/cards/agent-card";
import { Skeleton } from "@/components/ui/skeleton";
import { api } from "@/lib/api/client";
import { queryKeys } from "@/lib/api/query-keys";
import type { AgentResponse } from "@/lib/api/types";
import { cn } from "@/lib/utils";

// ---------------------------------------------------------------------------
// Types
// ---------------------------------------------------------------------------

type HostingFilter = "all" | "ephemeral" | "persistent";
type InitiativeFilter =
  | "all"
  | "Passive"
  | "Attentive"
  | "Proactive"
  | "Autonomous";

// ---------------------------------------------------------------------------
// FilterChip — v2 filter-bar primitive (matches /activity pattern).
// ---------------------------------------------------------------------------

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

// ---------------------------------------------------------------------------
// Agent card adapter
// ---------------------------------------------------------------------------

function agentToCardShape(agent: AgentResponse): AgentCardAgent {
  return {
    name: agent.name,
    displayName: agent.displayName,
    role: agent.role ?? null,
    registeredAt: agent.registeredAt,
    status: agent.enabled ? "running" : "stopped",
    parentUnit: agent.parentUnit ?? null,
    executionMode: agent.executionMode,
  };
}

// ---------------------------------------------------------------------------
// Page
// ---------------------------------------------------------------------------

function AgentsContent() {
  const [hostingFilter, setHostingFilter] = useState<HostingFilter>("all");
  const [initiativeFilter, setInitiativeFilter] =
    useState<InitiativeFilter>("all");

  const agentsQuery = useQuery({
    queryKey: queryKeys.agents.list(),
    queryFn: () => api.listAgents(),
  });

  const agents: AgentResponse[] = useMemo(
    () => agentsQuery.data ?? [],
    [agentsQuery.data],
  );

  // Client-side filtering. Server-side is tracked as follow-up #1402.
  const filtered = useMemo(() => {
    return agents.filter((a) => {
      const hostingOk =
        hostingFilter === "all" ||
        (a.hostingMode ?? "ephemeral").toLowerCase() ===
          hostingFilter.toLowerCase();
      const initiativeOk =
        initiativeFilter === "all" ||
        (a.initiativeLevel ?? "").toLowerCase() ===
          initiativeFilter.toLowerCase();
      return hostingOk && initiativeOk;
    });
  }, [agents, hostingFilter, initiativeFilter]);

  return (
    <div className="space-y-6">
      <div>
        <h1 className="flex items-center gap-2 text-2xl font-bold">
          <Bot className="h-5 w-5" aria-hidden="true" /> Agents
        </h1>
        <p className="text-sm text-muted-foreground">
          All agents registered in this tenant.
        </p>
      </div>

      {/* Filter bar */}
      <div
        className="flex flex-wrap gap-2"
        role="group"
        aria-label="Agent filters"
        data-testid="agents-filter-bar"
      >
        <FilterChip label="Hosting" active={hostingFilter !== "all"}>
          <select
            value={hostingFilter}
            onChange={(e) => setHostingFilter(e.target.value as HostingFilter)}
            className="bg-transparent focus:outline-none"
            aria-label="Hosting filter"
            data-testid="agents-hosting-filter"
          >
            <option value="all">All</option>
            <option value="ephemeral">Ephemeral</option>
            <option value="persistent">Persistent</option>
          </select>
        </FilterChip>

        <FilterChip label="Initiative" active={initiativeFilter !== "all"}>
          <select
            value={initiativeFilter}
            onChange={(e) =>
              setInitiativeFilter(e.target.value as InitiativeFilter)
            }
            className="bg-transparent focus:outline-none"
            aria-label="Initiative filter"
            data-testid="agents-initiative-filter"
          >
            <option value="all">All</option>
            <option value="Passive">Passive</option>
            <option value="Attentive">Attentive</option>
            <option value="Proactive">Proactive</option>
            <option value="Autonomous">Autonomous</option>
          </select>
        </FilterChip>
      </div>

      {/* Content */}
      {agentsQuery.isPending ? (
        <div className="space-y-3" data-testid="agents-loading">
          <Skeleton className="h-24" />
          <Skeleton className="h-24" />
          <Skeleton className="h-24" />
        </div>
      ) : agentsQuery.isError ? (
        <p
          role="alert"
          className="rounded-md border border-destructive/50 bg-destructive/10 px-3 py-2 text-sm text-destructive"
          data-testid="agents-error"
        >
          {agentsQuery.error instanceof Error
            ? agentsQuery.error.message
            : "Failed to load agents."}
        </p>
      ) : filtered.length === 0 ? (
        <p
          className="text-sm text-muted-foreground"
          data-testid="agents-empty"
        >
          {agents.length === 0
            ? "No agents registered. Create one from the Units Explorer."
            : "No agents match the current filters."}
        </p>
      ) : (
        <div
          className="grid gap-4 sm:grid-cols-2 lg:grid-cols-3"
          data-testid="agents-grid"
        >
          {filtered.map((agent) => (
            <AgentCard
              key={agent.name}
              agent={agentToCardShape(agent)}
            />
          ))}
        </div>
      )}

      <p className="text-xs text-muted-foreground">
        Open the{" "}
        <Link href="/units" className="text-primary hover:underline">
          Units Explorer
        </Link>{" "}
        for full per-agent detail, policies, and lifecycle controls.
      </p>
    </div>
  );
}

export default function AgentsPage() {
  return <AgentsContent />;
}
