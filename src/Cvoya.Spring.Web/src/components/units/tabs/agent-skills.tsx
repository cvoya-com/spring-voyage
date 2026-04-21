"use client";

// Agent Skills tab (EXP-tab-agent-skills, umbrella #815 §4 /
// QUALITY-agent-skills-write, #900).
//
// Writable list of the agent's equipped skills. Each current skill
// surfaces as a remove chip; an "Add skill" combobox seeded from the
// tenant catalog lets operators add the rest. All edits flow through
// `useSetAgentSkills(id)` — PUT is a full replacement, so the hook
// passes the complete post-edit list on every mutation, mirroring
// `spring agent skills set <agent> -- <skill>…`.
//
// Before #900 this tab was read-only; the legacy `/agents/[id]`
// Settings pane owned the edit affordance. Once `DEL-agents` (#870)
// lands, the legacy pane disappears and operators who need to edit
// skills from the portal land here.

import { useMemo, useState } from "react";
import { Sparkles, X } from "lucide-react";

import { Badge } from "@/components/ui/badge";
import { useToast } from "@/components/ui/toast";
import {
  useAgentSkills,
  useSetAgentSkills,
  useSkillsCatalog,
} from "@/lib/api/queries";

import { registerTab, type TabContentProps } from "./index";

function AgentSkillsTab({ node }: TabContentProps) {
  const { toast } = useToast();
  // Hooks run unconditionally — registry guarantees `kind === "Agent"`.
  const skillsQuery = useAgentSkills(node.id);
  const catalogQuery = useSkillsCatalog();
  const setSkills = useSetAgentSkills(node.id);
  const [selected, setSelected] = useState("");

  const skills = useMemo(
    () => skillsQuery.data?.skills ?? [],
    [skillsQuery.data],
  );

  // Catalog entries the agent hasn't already equipped. The server's
  // `PUT /skills` rejects unknown skill names, so the combobox only
  // surfaces catalog entries — no free-text add.
  const available = useMemo(() => {
    const equipped = new Set(skills);
    return (catalogQuery.data ?? []).filter((e) => !equipped.has(e.name));
  }, [catalogQuery.data, skills]);

  if (node.kind !== "Agent") return null;

  if (skillsQuery.isLoading) {
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

  if (skillsQuery.error) {
    return (
      <p
        role="alert"
        className="rounded-md border border-destructive/50 bg-destructive/10 px-3 py-2 text-sm text-destructive"
        data-testid="tab-agent-skills-error"
      >
        Couldn&apos;t load skills:{" "}
        {skillsQuery.error instanceof Error
          ? skillsQuery.error.message
          : String(skillsQuery.error)}
      </p>
    );
  }

  // Shared mutation dispatch used by both the add and remove paths.
  // PUT is a full replacement, so callers build the complete
  // post-mutation list and hand it in here.
  const commit = (next: string[], failureTitle: string) => {
    setSkills.mutate(next, {
      onError: (err) => {
        toast({
          title: failureTitle,
          description: err instanceof Error ? err.message : String(err),
          variant: "destructive",
        });
      },
    });
  };

  const handleAdd = (skillName: string) => {
    if (!skillName) return;
    if (skills.includes(skillName)) return;
    commit([...skills, skillName], "Failed to add skill");
    setSelected("");
  };

  const handleRemove = (skillName: string) => {
    const next = skills.filter((s) => s !== skillName);
    commit(next, "Failed to remove skill");
  };

  const mutating = setSkills.isPending;

  return (
    <div className="space-y-3" data-testid="tab-agent-skills">
      <p className="text-xs text-muted-foreground">
        {skills.length === 0
          ? "No skills equipped."
          : `${skills.length} skill${skills.length === 1 ? "" : "s"} equipped.`}{" "}
        Mirrors <code className="rounded bg-muted px-1 py-0.5 text-xs">spring agent skills</code>.
      </p>

      {skills.length === 0 ? (
        <div
          data-testid="tab-agent-skills-empty"
          className="rounded-lg border border-dashed border-border bg-muted/30 p-6 text-center"
        >
          <Sparkles
            className="mx-auto h-6 w-6 text-muted-foreground"
            aria-hidden="true"
          />
          <p className="mt-2 text-sm font-medium">No skills equipped</p>
          <p className="mt-1 text-xs text-muted-foreground">
            Pick one from the catalog below, or run{" "}
            <code className="rounded bg-muted px-1 py-0.5 text-xs">
              spring agent skills set
            </code>
            .
          </p>
        </div>
      ) : (
        <ul
          className="flex flex-wrap gap-2"
          aria-label={`Skills for agent ${node.name}`}
        >
          {skills.map((skill) => (
            <li key={skill}>
              <Badge
                variant="outline"
                className="gap-1 pr-1"
                data-testid={`tab-agent-skills-chip-${skill}`}
              >
                <Sparkles className="h-3 w-3" aria-hidden="true" />
                <span>{skill}</span>
                <button
                  type="button"
                  onClick={() => handleRemove(skill)}
                  disabled={mutating}
                  aria-label={`Remove skill ${skill}`}
                  className="ml-1 inline-flex h-4 w-4 items-center justify-center rounded-full text-muted-foreground hover:bg-muted hover:text-foreground disabled:cursor-not-allowed disabled:opacity-50"
                  data-testid={`tab-agent-skills-remove-${skill}`}
                >
                  <X className="h-3 w-3" aria-hidden="true" />
                </button>
              </Badge>
            </li>
          ))}
        </ul>
      )}

      <div className="flex items-center gap-2 pt-2">
        <label htmlFor="tab-agent-skills-add" className="sr-only">
          Add skill
        </label>
        <select
          id="tab-agent-skills-add"
          data-testid="tab-agent-skills-add"
          value={selected}
          disabled={
            mutating || catalogQuery.isLoading || available.length === 0
          }
          onChange={(e) => {
            const value = e.target.value;
            setSelected(value);
            handleAdd(value);
          }}
          className="flex h-9 rounded-md border border-input bg-background px-3 text-sm shadow-sm focus-visible:outline-none focus-visible:ring-1 focus-visible:ring-ring disabled:cursor-not-allowed disabled:opacity-50"
        >
          <option value="">
            {catalogQuery.isLoading
              ? "Loading catalog…"
              : available.length === 0
                ? "No skills left to add"
                : "Add skill…"}
          </option>
          {available.map((entry) => (
            <option key={entry.name} value={entry.name}>
              {entry.name}
              {entry.description ? ` — ${entry.description}` : ""}
            </option>
          ))}
        </select>
      </div>
    </div>
  );
}

registerTab("Agent", "Skills", AgentSkillsTab);

export default AgentSkillsTab;
