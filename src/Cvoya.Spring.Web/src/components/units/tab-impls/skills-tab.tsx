"use client";

import { useCallback, useEffect, useState } from "react";

import {
  Card,
  CardContent,
  CardHeader,
  CardTitle,
} from "@/components/ui/card";
import { useToast } from "@/components/ui/toast";
import { api } from "@/lib/api/client";
import type {
  AgentResponse,
  SkillCatalogEntry,
} from "@/lib/api/types";

interface SkillsTabProps {
  unitId: string;
}

/**
 * Skills tab for the unit configuration page. Lists the unit's agent
 * members and, for each, the full skill catalog grouped by registry with
 * checkbox toggles that reflect the agent's current skill set.
 *
 * Skills are agent-owned config (see #126) — edits fire at the agent-
 * scoped endpoint `PUT /api/v1/agents/{id}/skills`. Unit-level policy
 * enforcement (allow/block lists scoped to the whole unit) is a
 * separate concern tracked in #163; not in this tab.
 */
export function SkillsTab({ unitId }: SkillsTabProps) {
  const { toast } = useToast();
  const [agents, setAgents] = useState<AgentResponse[]>([]);
  const [catalog, setCatalog] = useState<SkillCatalogEntry[]>([]);
  // agentId → set of skill names the agent currently has enabled.
  const [agentSkills, setAgentSkills] = useState<Record<string, Set<string>>>(
    {},
  );
  const [loading, setLoading] = useState(true);
  const [loadError, setLoadError] = useState<string | null>(null);

  const load = useCallback(async () => {
    setLoadError(null);
    try {
      const [unitAgents, skillCatalog] = await Promise.all([
        api.listUnitAgents(unitId),
        api.listSkills(),
      ]);

      // Pull each agent's configured skill set in parallel. Unit sizes are
      // small (single-digit members typical), so N+1 is fine.
      const skillMap: Record<string, Set<string>> = {};
      await Promise.all(
        unitAgents.map(async (agent) => {
          try {
            const res = await api.getAgentSkills(agent.name);
            skillMap[agent.name] = new Set(res.skills);
          } catch {
            // A single-agent fetch failure shouldn't blank the whole tab;
            // fall back to an empty set and let the operator re-pick.
            skillMap[agent.name] = new Set();
          }
        }),
      );

      setAgents(unitAgents);
      setCatalog(skillCatalog);
      setAgentSkills(skillMap);
    } catch (err) {
      setLoadError(err instanceof Error ? err.message : String(err));
    } finally {
      setLoading(false);
    }
  }, [unitId]);

  useEffect(() => {
    load();
  }, [load]);

  const toggleSkill = async (
    agentId: string,
    skillName: string,
    enable: boolean,
  ) => {
    const current = agentSkills[agentId] ?? new Set<string>();
    const next = new Set(current);
    if (enable) {
      next.add(skillName);
    } else {
      next.delete(skillName);
    }

    // Optimistic update so the checkbox feels responsive; reconcile on
    // the server's returned list (PUT is a full replacement, so whatever
    // comes back is authoritative).
    setAgentSkills((prev) => ({ ...prev, [agentId]: next }));

    try {
      const res = await api.setAgentSkills(agentId, Array.from(next));
      setAgentSkills((prev) => ({
        ...prev,
        [agentId]: new Set(res.skills),
      }));
    } catch (err) {
      // Roll back the optimistic toggle on failure.
      setAgentSkills((prev) => ({ ...prev, [agentId]: current }));
      toast({
        title: "Skill update failed",
        description: err instanceof Error ? err.message : String(err),
        variant: "destructive",
      });
    }
  };

  // Group the catalog by registry for display (stable order matches
  // server output, which is ordinal-sorted).
  const byRegistry = new Map<string, SkillCatalogEntry[]>();
  for (const skill of catalog) {
    const list = byRegistry.get(skill.registry) ?? [];
    list.push(skill);
    byRegistry.set(skill.registry, list);
  }

  if (loadError) {
    return (
      <Card>
        <CardHeader>
          <CardTitle>Skills</CardTitle>
        </CardHeader>
        <CardContent>
          <p className="rounded-md border border-destructive/50 bg-destructive/10 px-3 py-2 text-sm text-destructive">
            {loadError}
          </p>
        </CardContent>
      </Card>
    );
  }

  if (loading) {
    return (
      <Card>
        <CardHeader>
          <CardTitle>Skills</CardTitle>
        </CardHeader>
        <CardContent>
          <p className="text-sm text-muted-foreground">Loading…</p>
        </CardContent>
      </Card>
    );
  }

  if (agents.length === 0) {
    return (
      <Card>
        <CardHeader>
          <CardTitle>Skills</CardTitle>
        </CardHeader>
        <CardContent>
          <p className="text-sm text-muted-foreground">
            Assign agents to this unit on the Agents tab before configuring
            skills.
          </p>
        </CardContent>
      </Card>
    );
  }

  return (
    <div className="space-y-4">
      {agents.map((agent) => {
        const enabled = agentSkills[agent.name] ?? new Set<string>();
        return (
          <Card key={agent.name}>
            <CardHeader>
              <CardTitle className="text-base">
                {agent.displayName || agent.name}
                <span className="ml-2 text-xs text-muted-foreground">
                  ({enabled.size} / {catalog.length} skill
                  {catalog.length === 1 ? "" : "s"})
                </span>
              </CardTitle>
            </CardHeader>
            <CardContent className="space-y-4">
              {Array.from(byRegistry.entries()).map(([registry, skills]) => (
                <div key={registry} className="space-y-2">
                  <p className="text-xs font-medium uppercase tracking-wide text-muted-foreground">
                    {registry}
                  </p>
                  <ul className="space-y-1">
                    {skills.map((skill) => (
                      <li key={skill.name}>
                        <label className="flex items-start gap-2 text-sm">
                          <input
                            type="checkbox"
                            checked={enabled.has(skill.name)}
                            onChange={(e) =>
                              toggleSkill(
                                agent.name,
                                skill.name,
                                e.target.checked,
                              )
                            }
                            className="mt-0.5"
                          />
                          <span>
                            <span className="font-mono text-xs">
                              {skill.name}
                            </span>
                            {skill.description && (
                              <span className="ml-2 text-muted-foreground">
                                — {skill.description}
                              </span>
                            )}
                          </span>
                        </label>
                      </li>
                    ))}
                  </ul>
                </div>
              ))}
            </CardContent>
          </Card>
        );
      })}
    </div>
  );
}
