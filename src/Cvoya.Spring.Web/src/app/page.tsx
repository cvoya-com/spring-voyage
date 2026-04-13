"use client";

import { useEffect, useState } from "react";
import { Bot, DollarSign, Network } from "lucide-react";
import { api } from "@/lib/api/client";
import type {
  AgentDashboardSummary,
  UnitDashboardSummary,
  CostDashboardSummary,
} from "@/lib/api/types";
import { formatCost } from "@/lib/utils";
import { AgentCard } from "@/components/agent-card";
import { UnitCard } from "@/components/unit-card";
import { StatCard } from "@/components/stat-card";
import { ActivityFeed } from "@/components/activity-feed";
import { Skeleton } from "@/components/ui/skeleton";
import { useActivityStream } from "@/hooks/use-activity-stream";

export default function DashboardPage() {
  const [agents, setAgents] = useState<AgentDashboardSummary[]>([]);
  const [units, setUnits] = useState<UnitDashboardSummary[]>([]);
  const [costs, setCosts] = useState<CostDashboardSummary | null>(null);
  const [loading, setLoading] = useState(true);
  const { events } = useActivityStream();

  useEffect(() => {
    let cancelled = false;

    async function load() {
      try {
        const [a, u, c] = await Promise.all([
          api.getDashboardAgents(),
          api.getDashboardUnits(),
          api.getDashboardCosts(),
        ]);
        if (!cancelled) {
          setAgents(a);
          setUnits(u);
          setCosts(c);
          setLoading(false);
        }
      } catch {
        if (!cancelled) setLoading(false);
      }
    }

    load();
    const interval = setInterval(load, 10_000);
    return () => {
      cancelled = true;
      clearInterval(interval);
    };
  }, []);

  return (
    <div className="space-y-6">
      <h1 className="text-2xl font-bold">Dashboard</h1>

      {/* Stat cards */}
      <div className="grid grid-cols-1 gap-4 sm:grid-cols-3">
        {loading ? (
          <>
            <Skeleton className="h-20" />
            <Skeleton className="h-20" />
            <Skeleton className="h-20" />
          </>
        ) : (
          <>
            <StatCard
              label="Agents"
              value={agents.length}
              icon={<Bot className="h-5 w-5" />}
            />
            <StatCard
              label="Units"
              value={units.length}
              icon={<Network className="h-5 w-5" />}
            />
            <StatCard
              label="Total Cost"
              value={formatCost(Number(costs?.totalCost ?? 0))} // decimal -> number (#181)
              icon={<DollarSign className="h-5 w-5" />}
            />
          </>
        )}
      </div>

      {/* Main content: agents/units + activity feed */}
      <div className="grid grid-cols-1 gap-6 lg:grid-cols-3">
        <div className="space-y-6 lg:col-span-2">
          {/* Agents */}
          <section>
            <h2 className="mb-3 text-lg font-semibold">Agents</h2>
            {loading ? (
              <div className="grid grid-cols-1 gap-3 sm:grid-cols-2">
                <Skeleton className="h-24" />
                <Skeleton className="h-24" />
              </div>
            ) : agents.length === 0 ? (
              <p className="text-sm text-muted-foreground">No agents registered</p>
            ) : (
              <div className="grid grid-cols-1 gap-3 sm:grid-cols-2">
                {agents.map((a) => (
                  <AgentCard key={a.name} agent={a} />
                ))}
              </div>
            )}
          </section>

          {/* Units */}
          <section>
            <h2 className="mb-3 text-lg font-semibold">Units</h2>
            {loading ? (
              <div className="grid grid-cols-1 gap-3 sm:grid-cols-2">
                <Skeleton className="h-24" />
                <Skeleton className="h-24" />
              </div>
            ) : units.length === 0 ? (
              <p className="text-sm text-muted-foreground">No units registered</p>
            ) : (
              <div className="grid grid-cols-1 gap-3 sm:grid-cols-2">
                {units.map((u) => (
                  <UnitCard key={u.name} unit={u} />
                ))}
              </div>
            )}
          </section>
        </div>

        {/* Activity feed sidebar */}
        <div>
          <ActivityFeed items={events.slice(0, 20)} />
        </div>
      </div>
    </div>
  );
}
