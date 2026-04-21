"use client";

/**
 * /settings/skills — tenant skill catalog (#863 / SET-skills).
 *
 * Lists every skill the platform-wide catalog reports, grouped by
 * registry. Mirrors `spring skill list` (read path: GET /api/v1/skills)
 * — assignment of skills to individual agents still happens on the
 * unit Explorer's Skills tab (`/units/[id]` → Skills), this surface is
 * the read-only catalog.
 *
 * Implementation note: the skill catalog endpoint has no dedicated
 * query hook yet (only `api.listSkills()`). We read it via `useQuery`
 * inline here rather than grow the queries module for a single
 * read-only view.
 */

import { GraduationCap } from "lucide-react";
import { useQuery } from "@tanstack/react-query";

import { Badge } from "@/components/ui/badge";
import { Card, CardContent, CardHeader, CardTitle } from "@/components/ui/card";
import { Skeleton } from "@/components/ui/skeleton";
import { api } from "@/lib/api/client";
import type { SkillCatalogEntry } from "@/lib/api/types";

export default function SettingsSkillsPage() {
  const query = useQuery({
    queryKey: ["skills", "catalog"] as const,
    queryFn: () => api.listSkills(),
  });
  const skills = query.data ?? [];
  const loading = query.isPending;

  // Group by registry so the catalog renders one section per source
  // (matches the per-registry grouping used on `/units/[id]` → Skills).
  const byRegistry = new Map<string, SkillCatalogEntry[]>();
  for (const skill of skills) {
    const list = byRegistry.get(skill.registry) ?? [];
    list.push(skill);
    byRegistry.set(skill.registry, list);
  }

  return (
    <div className="space-y-6">
      <div>
        <h1 className="flex items-center gap-2 text-2xl font-bold">
          <GraduationCap className="h-5 w-5" aria-hidden="true" /> Skills
        </h1>
        <p className="text-sm text-muted-foreground">
          Platform-wide skill catalog, grouped by registry. Mirrors{" "}
          <code className="rounded bg-muted px-1 py-0.5 text-xs">
            spring skill list
          </code>
          . Assign skills to individual agents from the Explorer&apos;s
          Skills tab.
        </p>
      </div>

      {loading ? (
        <div className="space-y-3">
          <Skeleton className="h-24" />
          <Skeleton className="h-24" />
        </div>
      ) : query.error ? (
        <Card>
          <CardContent className="p-6">
            <p className="text-sm text-destructive" role="alert">
              Failed to load skills: {query.error.message}
            </p>
          </CardContent>
        </Card>
      ) : skills.length === 0 ? (
        <Card>
          <CardContent className="p-6">
            <p className="text-sm text-muted-foreground">
              No skills registered. Skill registries ship inside packages
              — install a package with{" "}
              <code className="rounded bg-muted px-1 py-0.5 text-xs">
                spring package install
              </code>{" "}
              to populate the catalog.
            </p>
          </CardContent>
        </Card>
      ) : (
        <div className="space-y-4" data-testid="settings-skills-list">
          {Array.from(byRegistry.entries()).map(([registry, entries]) => (
            <Card
              key={registry}
              data-testid={`settings-skills-registry-${registry}`}
            >
              <CardHeader>
                <CardTitle className="flex items-center gap-2 text-base">
                  {registry}
                  <Badge variant="secondary">
                    {entries.length} skill{entries.length === 1 ? "" : "s"}
                  </Badge>
                </CardTitle>
              </CardHeader>
              <CardContent>
                <ul className="space-y-2">
                  {entries.map((skill) => (
                    <li
                      key={`${skill.registry}/${skill.name}`}
                      className="rounded-md border border-border bg-muted/30 p-3"
                    >
                      <div className="flex items-start gap-2">
                        <code className="font-mono text-xs text-foreground">
                          {skill.name}
                        </code>
                      </div>
                      {skill.description && (
                        <p className="mt-1 text-xs text-muted-foreground">
                          {skill.description}
                        </p>
                      )}
                    </li>
                  ))}
                </ul>
              </CardContent>
            </Card>
          ))}
        </div>
      )}
    </div>
  );
}
