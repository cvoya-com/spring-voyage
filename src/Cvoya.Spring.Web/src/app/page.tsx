"use client";

import { useEffect, useState } from "react";
import { Bot, DollarSign, Network } from "lucide-react";
import { api } from "@/lib/api/client";
import type { DashboardSummary } from "@/lib/api/types";
import { formatCost } from "@/lib/utils";
import { StatCard } from "@/components/stat-card";
import { Badge } from "@/components/ui/badge";
import { ActivityFeed } from "@/components/activity-feed";
import { Skeleton } from "@/components/ui/skeleton";
import { useActivityStream } from "@/hooks/use-activity-stream";

const statusVariant: Record<
  string,
  "default" | "success" | "warning" | "destructive" | "secondary" | "outline"
> = {
  Draft: "outline",
  Stopped: "secondary",
  Starting: "default",
  Running: "success",
  Stopping: "warning",
  Error: "destructive",
};

export default function DashboardPage() {
  const [summary, setSummary] = useState<DashboardSummary | null>(null);
  const [loading, setLoading] = useState(true);
  const { events } = useActivityStream();

  useEffect(() => {
    let cancelled = false;

    async function load() {
      try {
        const s = await api.getDashboardSummary();
        if (!cancelled) {
          setSummary(s);
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
              value={summary?.agentCount ?? 0}
              icon={<Bot className="h-5 w-5" />}
            />
            <StatCard
              label="Units"
              value={summary?.unitCount ?? 0}
              icon={<Network className="h-5 w-5" />}
            />
            <StatCard
              label="Total Cost"
              value={formatCost(summary?.totalCost ?? 0)}
              icon={<DollarSign className="h-5 w-5" />}
            />
          </>
        )}
      </div>

      {/* Unit status breakdown */}
      {!loading && summary && summary.unitCount > 0 && (
        <section>
          <h2 className="mb-3 text-lg font-semibold">Units by Status</h2>
          <div className="flex flex-wrap gap-2">
            {Object.entries(summary.unitsByStatus).map(([status, count]) => (
              <Badge
                key={status}
                variant={statusVariant[status] ?? "outline"}
              >
                {status}: {count}
              </Badge>
            ))}
          </div>
        </section>
      )}

      {/* Activity feed */}
      <div>
        <ActivityFeed items={events.slice(0, 20)} />
      </div>
    </div>
  );
}
