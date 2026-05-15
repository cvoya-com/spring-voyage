"use client";

// Unified equipped-Skills tab (canonical-tabs.md § 5.6, #2271).
//
// Renders the equipped-skills surface — chip list with remove, plus an
// "Add skill" combobox seeded from the tenant catalog. The Skills tab
// is the **editor** for one subject's equipped skills; the catalog at
// `/settings/skills` is the tenant-wide read-only roll-up.
//
// The canonical control accepts `{ kind, id }`. Today the underlying
// hooks are agent-keyed (`useAgentSkills`, `useSetAgentSkills`,
// `useSkillsCatalog`) — for `kind === "Unit"` the body renders a
// "Manage via CLI" placeholder until #2276 lands unit-keyed endpoints
// (see canonical-tabs.md § 5.6 for the design intent: "same control,
// re-parameterised by subject"). The placeholder preserves the
// canonical tab *position* on Unit even with the deferred body.
//
// Per canonical-tabs.md § 4 row 6 and `docs/concepts/units-vs-agents.md`
// rule 3, a unit is an agent and the Skills surface applies identically
// to both subjects — the deferral is an endpoint-side gap, not a
// domain-model one.

import { useMemo, useState } from "react";
import { Sparkles, X } from "lucide-react";

import { ApiErrorMessage } from "@/components/ui/api-error-message";
import { Badge } from "@/components/ui/badge";
import { useToast } from "@/components/ui/toast";
import { formatTranslatedError } from "@/lib/api/translate-error";
import {
  useAgentSkills,
  useSetAgentSkills,
  useSkillsCatalog,
  useUnitSkills,
  useSetUnitSkills,
} from "@/lib/api/queries";

export type EquippedSkillsSubjectKind = "Unit" | "Agent";

export interface EquippedSkillsTabProps {
  /** Subject kind — drives the skills hooks + the empty-state copy. */
  kind: EquippedSkillsSubjectKind;
  /** Stable id of the subject (unit id or agent id). */
  id: string;
  /** Display name used in the aria-label for the chip list. */
  name: string;
}

export function EquippedSkillsTab({
  kind,
  id,
  name,
}: EquippedSkillsTabProps) {
  if (kind === "Unit") {
    return <UnitSkillsBody unitId={id} unitName={name} />;
  }
  return <AgentSkillsBody agentId={id} agentName={name} />;
}

function UnitSkillsBody({
  unitId,
  unitName,
}: {
  unitId: string;
  unitName: string;
}) {
  const { toast } = useToast();
  const skillsQuery = useUnitSkills(unitId);
  const catalogQuery = useSkillsCatalog();
  const setSkills = useSetUnitSkills(unitId);
  const [selected, setSelected] = useState("");

  const skills = useMemo(
    () => skillsQuery.data?.skills ?? [],
    [skillsQuery.data],
  );

  const available = useMemo(() => {
    const equipped = new Set(skills);
    return (catalogQuery.data ?? []).filter((e) => !equipped.has(e.name));
  }, [catalogQuery.data, skills]);

  if (skillsQuery.isLoading) {
    return (
      <p
        role="status"
        aria-live="polite"
        className="text-sm text-muted-foreground"
        data-testid="tab-unit-skills-loading"
      >
        Loading skills…
      </p>
    );
  }

  if (skillsQuery.error) {
    return (
      <div data-testid="tab-unit-skills-error">
        <ApiErrorMessage error={skillsQuery.error} />
      </div>
    );
  }

  const commit = (next: string[], failureTitle: string) => {
    setSkills.mutate(next, {
      onError: (err) => {
        toast({
          title: failureTitle,
          description: formatTranslatedError(err),
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
    <div className="space-y-3" data-testid="tab-unit-skills">
      <p className="text-xs text-muted-foreground">
        {skills.length === 0
          ? "No skills equipped."
          : `${skills.length} skill${skills.length === 1 ? "" : "s"} equipped.`}{" "}
        Mirrors <code className="rounded bg-muted px-1 py-0.5 text-xs">spring agent skills</code>.
      </p>

      {skills.length === 0 ? (
        <div
          data-testid="tab-unit-skills-empty"
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
          aria-label={`Skills for unit ${unitName}`}
        >
          {skills.map((skill) => (
            <li key={skill}>
              <Badge
                variant="outline"
                className="gap-1 pr-1"
                data-testid={`tab-unit-skills-chip-${skill}`}
              >
                <Sparkles className="h-3 w-3" aria-hidden="true" />
                <span>{skill}</span>
                <button
                  type="button"
                  onClick={() => handleRemove(skill)}
                  disabled={mutating}
                  aria-label={`Remove skill ${skill}`}
                  className="ml-1 inline-flex h-4 w-4 items-center justify-center rounded-full text-muted-foreground hover:bg-muted hover:text-foreground disabled:cursor-not-allowed disabled:opacity-50"
                  data-testid={`tab-unit-skills-remove-${skill}`}
                >
                  <X className="h-3 w-3" />
                </button>
              </Badge>
            </li>
          ))}
        </ul>
      )}

      <div className="flex items-center gap-2 pt-2">
        <label htmlFor="tab-unit-skills-add" className="sr-only">
          Add skill
        </label>
        <select
          id="tab-unit-skills-add"
          data-testid="tab-unit-skills-add"
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

function AgentSkillsBody({
  agentId,
  agentName,
}: {
  agentId: string;
  agentName: string;
}) {
  const { toast } = useToast();
  const skillsQuery = useAgentSkills(agentId);
  const catalogQuery = useSkillsCatalog();
  const setSkills = useSetAgentSkills(agentId);
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
      <div data-testid="tab-agent-skills-error">
        <ApiErrorMessage error={skillsQuery.error} />
      </div>
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
          description: formatTranslatedError(err),
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
          aria-label={`Skills for agent ${agentName}`}
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
