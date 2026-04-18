"use client";

/**
 * Agent expertise panel (#486). Drop into the agent detail page — it
 * reads and writes `/api/v1/agents/{id}/expertise`, the same endpoint
 * `spring agent expertise {get|set}` targets on the CLI.
 */

import { useQueryClient } from "@tanstack/react-query";
import { GraduationCap } from "lucide-react";

import { ExpertiseEditor } from "./expertise-editor";

import {
  Card,
  CardContent,
  CardHeader,
  CardTitle,
} from "@/components/ui/card";
import { Skeleton } from "@/components/ui/skeleton";
import { api } from "@/lib/api/client";
import { useAgentExpertise } from "@/lib/api/queries";
import { queryKeys } from "@/lib/api/query-keys";
import type { ExpertiseDomainDto } from "@/lib/api/types";

interface AgentExpertisePanelProps {
  agentId: string;
}

export function AgentExpertisePanel({ agentId }: AgentExpertisePanelProps) {
  const queryClient = useQueryClient();
  const expertiseQuery = useAgentExpertise(agentId);
  const domains = expertiseQuery.data ?? [];

  const handleSave = async (
    next: ExpertiseDomainDto[],
  ): Promise<ExpertiseDomainDto[]> => {
    const updated = await api.setAgentExpertise(agentId, next);
    // Seed the cache so the editor reflects the server-authoritative
    // payload without a round trip, and invalidate every aggregated
    // slice since the agent's new list ripples up to every unit that
    // includes it. We can't narrow by ancestor without walking the
    // hierarchy, so we invalidate the whole unit-aggregated surface.
    queryClient.setQueryData(queryKeys.agents.expertise(agentId), updated);
    await queryClient.invalidateQueries({
      queryKey: ["units", "aggregatedExpertise"],
    });
    await queryClient.invalidateQueries({ queryKey: queryKeys.directory.all });
    return updated;
  };

  return (
    <Card>
      <CardHeader>
        <CardTitle className="flex items-center gap-2">
          <GraduationCap className="h-4 w-4" /> Expertise
        </CardTitle>
      </CardHeader>
      <CardContent className="space-y-3">
        <p className="text-xs text-muted-foreground">
          Capabilities the agent advertises to the directory. The list is
          auto-seeded from the agent YAML on first activation and can be
          edited here afterwards. Matches{" "}
          <code className="rounded bg-muted px-1 py-0.5 text-xs">
            spring agent expertise set
          </code>
          .
        </p>
        {expertiseQuery.isPending ? (
          <Skeleton className="h-20" />
        ) : expertiseQuery.error ? (
          <p className="rounded-md border border-destructive/50 bg-destructive/10 px-3 py-2 text-sm text-destructive">
            {expertiseQuery.error.message}
          </p>
        ) : (
          <ExpertiseEditor domains={domains} onSave={handleSave} />
        )}
      </CardContent>
    </Card>
  );
}
