"use client";

import { Badge } from "@/components/ui/badge";
import { Card, CardContent, CardHeader, CardTitle } from "@/components/ui/card";
import type { AgentDashboardSummary } from "@/lib/api/types";
import { timeAgo } from "@/lib/utils";
import Link from "next/link";

export function AgentCard({ agent }: { agent: AgentDashboardSummary }) {
  return (
    <Link href={`/agents/${encodeURIComponent(agent.name)}`}>
      <Card className="transition-colors hover:bg-accent/50">
        <CardHeader className="flex flex-row items-center justify-between space-y-0 pb-2">
          <div className="space-y-1">
            <CardTitle>{agent.displayName}</CardTitle>
            {agent.role && (
              <Badge variant="outline" className="text-[10px]">
                {agent.role}
              </Badge>
            )}
          </div>
        </CardHeader>
        <CardContent>
          <p className="text-xs text-muted-foreground">
            Registered {timeAgo(agent.registeredAt)}
          </p>
        </CardContent>
      </Card>
    </Link>
  );
}
