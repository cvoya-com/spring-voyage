"use client";

// Equipped-skills surface (#2362). The Skills sub-tab body for both
// Unit and Agent subjects. Two sections:
//
//   1. Equipped — currently equipped bundles, with a Remove button per
//      row. For the Agent variant we also overlay rows inherited from
//      the parent unit's equipped list (Layer 2 inheritance, today
//      surfaced via a parallel `useEquippedSkills('unit', parentUnitId)`
//      call). Once #2363 lands the server projects these into the
//      agent's own response and the parent-unit fetch drops away —
//      kept here so the inheritance UX is correct from day one.
//
//   2. Equip a skill — a button that opens a dialog enumerating every
//      `kind: Skill` bundle across every installed package
//      (`useAvailableSkills`). Each row carries name + source-package
//      + Equip button. Bundles already equipped on the subject (or
//      inherited from the parent unit) are disabled with an in-place
//      pill so operators don't double-equip.
//
// Inheritance overlay mirrors the `<ToolsPanel>` connector tier
// (#2347): inherited rows render at `opacity-60`, carry an
// `Inherited from <unit>` badge linking back to the parent unit's
// Skills sub-tab, and offer no Remove affordance (operator must visit
// the source unit to detach).
//
// v0.1 ships read-from-package — there is no inline body editor. The
// `promptSummary` on each equipped row is a server-truncated preview;
// the full body lives in the package on disk.

import { Link2, Plus, Sparkles, Trash2 } from "lucide-react";
import { useMemo, useState } from "react";

import { ApiErrorMessage } from "@/components/ui/api-error-message";
import { Badge } from "@/components/ui/badge";
import { Button } from "@/components/ui/button";
import { ConfirmDialog } from "@/components/ui/confirm-dialog";
import { Dialog } from "@/components/ui/dialog";
import { Input } from "@/components/ui/input";
import {
  useAgent,
  useAvailableSkills,
  useEquipSkill,
  useEquippedSkills,
  useUnequipSkill,
} from "@/lib/api/queries";
import type {
  EquippedSkillEntry,
  SkillSummary,
} from "@/lib/api/types";
import { toExplorerPathSegment } from "@/lib/explorer-url";

export type EquippedSkillsSubjectKind = "Unit" | "Agent";

export interface EquippedSkillsTabProps {
  kind: EquippedSkillsSubjectKind;
  id: string;
  name: string;
}

export function EquippedSkillsTab({ kind, id }: EquippedSkillsTabProps) {
  return kind === "Unit" ? (
    <UnitSkillsView id={id} />
  ) : (
    <AgentSkillsView id={id} />
  );
}

// ---------------------------------------------------------------------------
// Per-kind wrappers — own the data fetch (and, for Agent, the parent-unit
// overlay) and hand a uniform `EquippedSkillsBody` to the renderer.
// ---------------------------------------------------------------------------

function UnitSkillsView({ id }: { id: string }) {
  const equipped = useEquippedSkills("unit", id);

  if (equipped.isLoading) {
    return <LoadingState testId="tab-unit-skills-loading" />;
  }
  if (equipped.error) {
    return (
      <div data-testid="tab-unit-skills-error">
        <ApiErrorMessage error={equipped.error} />
      </div>
    );
  }

  return (
    <EquippedSkillsBody
      testId="tab-unit-skills"
      scope="unit"
      id={id}
      equipped={equipped.data?.skills ?? []}
      inherited={[]}
      parentUnitName={null}
      parentUnitId={null}
    />
  );
}

function AgentSkillsView({ id }: { id: string }) {
  const equipped = useEquippedSkills("agent", id);
  const agent = useAgent(id);

  // Parent-unit overlay (#2362 / awaiting backend #2363). Today the
  // server doesn't project unit-equipped skills onto the agent's own
  // response, so we fetch them in parallel and render the inherited
  // rows greyed-out. Once #2363 lands the server-side projection takes
  // over; this hook drops to a no-op (returns the same row already
  // present in `equipped`) and the inheritance overlay still renders
  // correctly because direct-takes-precedence dedup keeps a single row
  // per `<pkg>/<skill>` key.
  const parentUnitId = agent.data?.agent.parentUnitId ?? null;
  const parentUnitName = agent.data?.agent.parentUnit ?? null;
  const inheritedQuery = useEquippedSkills("unit", parentUnitId ?? "", {
    enabled: Boolean(parentUnitId),
  });

  if (equipped.isLoading || agent.isLoading) {
    return <LoadingState testId="tab-agent-skills-loading" />;
  }
  if (equipped.error) {
    return (
      <div data-testid="tab-agent-skills-error">
        <ApiErrorMessage error={equipped.error} />
      </div>
    );
  }

  return (
    <EquippedSkillsBody
      testId="tab-agent-skills"
      scope="agent"
      id={id}
      equipped={equipped.data?.skills ?? []}
      inherited={inheritedQuery.data?.skills ?? []}
      parentUnitName={parentUnitName}
      parentUnitId={parentUnitId}
    />
  );
}

function LoadingState({ testId }: { testId: string }) {
  return (
    <p
      role="status"
      aria-live="polite"
      className="text-sm text-muted-foreground"
      data-testid={testId}
    >
      Loading skills…
    </p>
  );
}

// ---------------------------------------------------------------------------
// Body — kind-agnostic; renders the equipped + inherited rows and owns
// the equip / unequip dialog state.
// ---------------------------------------------------------------------------

interface EquippedSkillsBodyProps {
  testId: string;
  scope: "unit" | "agent";
  id: string;
  equipped: readonly EquippedSkillEntry[];
  inherited: readonly EquippedSkillEntry[];
  parentUnitName: string | null;
  parentUnitId: string | null;
}

function EquippedSkillsBody({
  testId,
  scope,
  id,
  equipped,
  inherited,
  parentUnitName,
  parentUnitId,
}: EquippedSkillsBodyProps) {
  const [drawerOpen, setDrawerOpen] = useState(false);
  const [pendingRemove, setPendingRemove] =
    useState<EquippedSkillEntry | null>(null);

  const equipMutation = useEquipSkill(scope, id);
  const unequipMutation = useUnequipSkill(scope, id);

  // Filter the inherited list: drop entries the subject also equips
  // directly (direct beats inherited; the row already appears in the
  // equipped section). Membership is keyed on `<pkg>/<skill>`.
  const directKeys = useMemo(
    () => new Set(equipped.map((s) => skillKey(s))),
    [equipped],
  );
  const inheritedOnly = useMemo(
    () => inherited.filter((s) => !directKeys.has(skillKey(s))),
    [inherited, directKeys],
  );

  const totalCount = equipped.length + inheritedOnly.length;
  const equippedKeys = useMemo(
    () => new Set([...directKeys, ...inheritedOnly.map((s) => skillKey(s))]),
    [directKeys, inheritedOnly],
  );

  return (
    <div className="space-y-4" data-testid={testId}>
      <header className="flex items-start justify-between gap-2">
        <p className="text-xs text-muted-foreground">
          Skills are authored capabilities shipped in packages (
          <code className="rounded bg-muted px-1 py-0.5 text-xs">kind: Skill</code>
          ). Each equipped bundle contributes a prompt fragment to{" "}
          {scope === "unit"
            ? "Layer 2 (unit context)"
            : "Layer 4 (agent instructions)"}{" "}
          of every turn.
        </p>
        <Button
          size="sm"
          variant="default"
          onClick={() => setDrawerOpen(true)}
          data-testid={`${testId}-equip-open`}
        >
          <Plus className="mr-1 h-3.5 w-3.5" aria-hidden="true" />
          Equip a skill
        </Button>
      </header>

      {totalCount === 0 ? (
        <div
          className="rounded-lg border border-dashed border-border bg-muted/30 p-6 text-center"
          data-testid={`${testId}-empty`}
        >
          <Sparkles
            className="mx-auto h-6 w-6 text-muted-foreground"
            aria-hidden="true"
          />
          <p className="mt-2 text-sm font-medium">No skills equipped</p>
          <p className="mt-1 text-xs text-muted-foreground">
            Click <em>Equip a skill</em> to browse bundles shipped by your
            installed packages.
          </p>
        </div>
      ) : (
        <ul
          className="divide-y divide-border rounded-md border border-border"
          data-testid={`${testId}-list`}
        >
          {equipped.map((skill) => (
            <EquippedRow
              key={skillKey(skill)}
              testId={testId}
              skill={skill}
              variant="direct"
              parentUnitName={null}
              parentUnitId={null}
              onRemove={() => setPendingRemove(skill)}
            />
          ))}
          {inheritedOnly.map((skill) => (
            <EquippedRow
              key={`inherited-${skillKey(skill)}`}
              testId={testId}
              skill={skill}
              variant="inherited"
              parentUnitName={parentUnitName}
              parentUnitId={parentUnitId}
              onRemove={null}
            />
          ))}
        </ul>
      )}

      <EquipDrawer
        open={drawerOpen}
        onClose={() => setDrawerOpen(false)}
        testId={testId}
        alreadyEquippedKeys={equippedKeys}
        onEquip={async (pkg, skill) => {
          await equipMutation.mutateAsync({
            packageName: pkg,
            skillName: skill,
          });
          setDrawerOpen(false);
        }}
        equipPending={equipMutation.isPending}
        equipError={equipMutation.error}
      />

      {pendingRemove ? (
        <ConfirmDialog
          open
          title="Unequip skill"
          description={`Remove ${skillKey(pendingRemove)}? The next turn will assemble its prompt without this bundle.`}
          confirmLabel="Unequip"
          onCancel={() => setPendingRemove(null)}
          onConfirm={async () => {
            const target = pendingRemove;
            try {
              await unequipMutation.mutateAsync({
                packageName: target.packageName,
                skillName: target.skillName,
              });
            } finally {
              setPendingRemove(null);
            }
          }}
          pending={unequipMutation.isPending}
        />
      ) : null}
    </div>
  );
}

// ---------------------------------------------------------------------------
// Row — direct (with Remove button) or inherited (greyed-out + badge).
// ---------------------------------------------------------------------------

interface EquippedRowProps {
  testId: string;
  skill: EquippedSkillEntry;
  variant: "direct" | "inherited";
  parentUnitName: string | null;
  parentUnitId: string | null;
  onRemove: (() => void) | null;
}

function EquippedRow({
  testId,
  skill,
  variant,
  parentUnitName,
  parentUnitId,
  onRemove,
}: EquippedRowProps) {
  const isInherited = variant === "inherited";
  const rowClass = isInherited ? "opacity-60" : "";
  const inheritedLink =
    isInherited && parentUnitId
      ? `/explorer/units/${encodeURIComponent(toExplorerPathSegment(parentUnitId))}?tab=Config&subtab=Skills`
      : null;

  const key = skillKey(skill);
  const rowTestId = sanitizeTestId(
    `${testId}-row-${skill.packageName}-${skill.skillName}`,
  );

  return (
    <li
      className={`flex items-start gap-3 px-3 py-3 text-sm ${rowClass}`}
      data-testid={rowTestId}
      data-inherited={isInherited ? "true" : "false"}
    >
      <div className="min-w-0 flex-1 space-y-1">
        <div className="flex flex-wrap items-center gap-2">
          <code className="font-mono text-xs">{key}</code>
          {isInherited ? (
            <Badge variant="outline" data-testid={`${rowTestId}-inherited`}>
              {inheritedLink ? (
                <a
                  href={inheritedLink}
                  className="inline-flex items-center gap-1 underline"
                >
                  <Link2 className="h-3 w-3" aria-hidden="true" />
                  Inherited from {parentUnitName ?? "parent unit"}
                </a>
              ) : (
                <span className="inline-flex items-center gap-1">
                  <Link2 className="h-3 w-3" aria-hidden="true" />
                  Inherited from {parentUnitName ?? "parent unit"}
                </span>
              )}
            </Badge>
          ) : null}
          {skill.requiredTools.length > 0 ? (
            <span className="text-xs text-muted-foreground">
              {skill.requiredTools.length} tool
              {skill.requiredTools.length === 1 ? "" : "s"}
            </span>
          ) : null}
        </div>
        {skill.promptSummary ? (
          <p className="text-xs text-muted-foreground">
            {skill.promptSummary}
          </p>
        ) : null}
      </div>
      {onRemove ? (
        <Button
          variant="outline"
          size="sm"
          onClick={onRemove}
          data-testid={`${rowTestId}-remove`}
          aria-label={`Unequip ${key}`}
        >
          <Trash2 className="h-3.5 w-3.5" aria-hidden="true" />
        </Button>
      ) : null}
    </li>
  );
}

// ---------------------------------------------------------------------------
// Equip drawer — lists every available skill across installed packages.
// ---------------------------------------------------------------------------

interface EquipDrawerProps {
  open: boolean;
  onClose: () => void;
  testId: string;
  alreadyEquippedKeys: ReadonlySet<string>;
  onEquip: (packageName: string, skillName: string) => Promise<void>;
  equipPending: boolean;
  equipError: Error | null;
}

function EquipDrawer({
  open,
  onClose,
  testId,
  alreadyEquippedKeys,
  onEquip,
  equipPending,
  equipError,
}: EquipDrawerProps) {
  const [filter, setFilter] = useState("");
  const available = useAvailableSkills({ enabled: open });

  const filtered = useMemo(() => {
    const rows = available.data ?? [];
    if (!filter.trim()) return rows;
    const needle = filter.trim().toLowerCase();
    return rows.filter(
      (s) =>
        s.name.toLowerCase().includes(needle) ||
        s.package.toLowerCase().includes(needle),
    );
  }, [available.data, filter]);

  return (
    <Dialog
      open={open}
      onClose={onClose}
      title="Equip a skill"
      description="Pick a skill bundle from your installed packages."
      className="max-w-2xl"
      footer={
        <Button variant="outline" onClick={onClose} disabled={equipPending}>
          Close
        </Button>
      }
    >
      <div className="space-y-3" data-testid={`${testId}-equip-drawer`}>
        <Input
          type="search"
          placeholder="Filter by name or package…"
          value={filter}
          onChange={(e) => setFilter(e.target.value)}
          data-testid={`${testId}-equip-filter`}
        />

        {available.isLoading ? (
          <p
            className="text-sm text-muted-foreground"
            role="status"
            aria-live="polite"
            data-testid={`${testId}-equip-loading`}
          >
            Loading available skills…
          </p>
        ) : available.error ? (
          <ApiErrorMessage error={available.error} />
        ) : filtered.length === 0 ? (
          <p
            className="rounded-md border border-dashed border-border bg-muted/10 px-3 py-3 text-sm text-muted-foreground"
            data-testid={`${testId}-equip-empty`}
          >
            {filter.trim()
              ? "No skills match the filter."
              : "No skills are available. Install a package that ships kind: Skill bundles to see options here."}
          </p>
        ) : (
          <ul
            className="max-h-96 divide-y divide-border overflow-y-auto rounded-md border border-border"
            data-testid={`${testId}-equip-list`}
          >
            {filtered.map((row) => (
              <AvailableSkillRow
                key={`${row.package}/${row.name}`}
                testId={testId}
                row={row}
                alreadyEquipped={alreadyEquippedKeys.has(
                  `${row.package}/${row.name}`,
                )}
                onEquip={() => onEquip(row.package, row.name)}
                pending={equipPending}
              />
            ))}
          </ul>
        )}

        {equipError ? <ApiErrorMessage error={equipError} /> : null}
      </div>
    </Dialog>
  );
}

function AvailableSkillRow({
  testId,
  row,
  alreadyEquipped,
  onEquip,
  pending,
}: {
  testId: string;
  row: SkillSummary;
  alreadyEquipped: boolean;
  onEquip: () => void;
  pending: boolean;
}) {
  const key = `${row.package}/${row.name}`;
  const rowTestId = sanitizeTestId(`${testId}-equip-row-${key}`);
  return (
    <li className="flex items-center gap-3 px-3 py-2" data-testid={rowTestId}>
      <div className="min-w-0 flex-1 space-y-0.5">
        <code className="font-mono text-xs">{key}</code>
        {row.hasTools ? (
          <p className="text-xs text-muted-foreground">
            Declares required tools.
          </p>
        ) : null}
      </div>
      {alreadyEquipped ? (
        <Badge variant="secondary" data-testid={`${rowTestId}-equipped`}>
          Equipped
        </Badge>
      ) : (
        <Button
          size="sm"
          variant="outline"
          onClick={onEquip}
          disabled={pending}
          data-testid={`${rowTestId}-equip`}
        >
          Equip
        </Button>
      )}
    </li>
  );
}

// ---------------------------------------------------------------------------
// Helpers
// ---------------------------------------------------------------------------

function skillKey(skill: EquippedSkillEntry): string {
  return `${skill.packageName}/${skill.skillName}`;
}

/**
 * Make a string safe for a `data-testid` slot. Package + skill names
 * are operator-controlled — characters outside the safe set get
 * collapsed to `_` so query selectors stay simple.
 */
function sanitizeTestId(value: string): string {
  return value.replace(/[^a-zA-Z0-9._:-]/g, "_");
}
