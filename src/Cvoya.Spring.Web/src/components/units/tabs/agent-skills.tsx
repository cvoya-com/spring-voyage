"use client";

// Agent Skills tab (EXP-tab-agent-skills, umbrella #815 §4).
//
// Read-only view of the agent's currently-equipped skills. The CLI and
// the unit-side Skills tab (`/units/[id]?tab=skills`) own the editing
// surface; this Explorer tab is a quick-reference list so operators
// don't have to navigate to the owning unit to see an agent's toolset.

import { useQuery } from "@tanstack/react-query";
import { Sparkles } from "lucide-react";

import { Badge } from "@/components/ui/badge";
import { api } from "@/lib/api/client";

import { registerTab, type TabContentProps } from "./index";

function AgentSkillsTab({ node }: TabContentProps) {
  // Hook runs unconditionally — registry guarantees `kind === "Agent"`.
  const { data, isLoading, error } = useQuery({
    queryKey: ["agents", node.id, "skills"] as const,
    queryFn: () => api.getAgentSkills(node.id),
    enabled: Boolean(node.id),
  });
  if (node.kind !== "Agent") return null;

  if (isLoading) {
    return (
      <p
        role="status"
        aria-live="polite"
        className="text-sm text-muted-foreground"
        data-testid="tab-agent-skills-loading"
      >
        Loading skills…
      </p>
    );
  }

  if (error) {
    return (
      <p
        role="alert"
        className="rounded-md border border-destructive/50 bg-destructive/10 px-3 py-2 text-sm text-destructive"
        data-testid="tab-agent-skills-error"
      >
        Couldn&apos;t load skills:{" "}
        {error instanceof Error ? error.message : String(error)}
      </p>
    );
  }

  const skills = data?.skills ?? [];

  if (skills.length === 0) {
    return (
      <div
        data-testid="tab-agent-skills-empty"
        className="rounded-lg border border-dashed border-border bg-muted/30 p-6 text-center"
      >
        <Sparkles className="mx-auto h-6 w-6 text-muted-foreground" aria-hidden="true" />
        <p className="mt-2 text-sm font-medium">No skills equipped</p>
        <p className="mt-1 text-xs text-muted-foreground">
          Assign skills from the owning unit&apos;s Skills tab, or via{" "}
          <code className="rounded bg-muted px-1 py-0.5 text-xs">
            spring agent skills set
          </code>
          .
        </p>
      </div>
    );
  }

  return (
    <div className="space-y-3" data-testid="tab-agent-skills">
      <p className="text-xs text-muted-foreground">
        {skills.length} skill{skills.length === 1 ? "" : "s"} equipped. Edit
        from the owning unit&apos;s Skills tab.
      </p>
      <ul
        className="flex flex-wrap gap-2"
        aria-label={`Skills for agent ${node.name}`}
      >
        {skills.map((skill) => (
          <li key={skill}>
            <Badge variant="outline" className="gap-1">
              <Sparkles className="h-3 w-3" aria-hidden="true" />
              {skill}
            </Badge>
          </li>
        ))}
      </ul>
    </div>
  );
}

registerTab("Agent", "Skills", AgentSkillsTab);

export default AgentSkillsTab;
