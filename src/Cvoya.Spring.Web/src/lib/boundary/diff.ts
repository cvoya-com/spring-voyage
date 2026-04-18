// Boundary diff helpers for the portal's YAML upload preview (#524).
//
// Before the operator commits an uploaded boundary, the tab shows a
// side-by-side summary of what is currently persisted vs. what the YAML
// declares. We avoid a structural diff library (there's no production-
// grade one in the existing deps) and instead compare the rule lists
// position-by-position using stable string keys — the same rule-identity
// model the CLI's `spring unit boundary get` printer uses when it emits
// one line per rule.

import type {
  BoundaryOpacityRuleDto,
  BoundaryProjectionRuleDto,
  BoundarySynthesisRuleDto,
  UnitBoundaryResponse,
} from "@/lib/api/types";

export type RuleKind = "opacity" | "projection" | "synthesis";

/** Single-line identity for one rule. Stable across null/"(any)" framing. */
function opacityKey(rule: BoundaryOpacityRuleDto): string {
  return `domain=${rule.domainPattern ?? ""}|origin=${rule.originPattern ?? ""}`;
}

function projectionKey(rule: BoundaryProjectionRuleDto): string {
  return (
    `domain=${rule.domainPattern ?? ""}|origin=${rule.originPattern ?? ""}` +
    `|rename=${rule.renameTo ?? ""}|retag=${rule.retag ?? ""}` +
    `|level=${rule.overrideLevel ?? ""}`
  );
}

function synthesisKey(rule: BoundarySynthesisRuleDto): string {
  return (
    `name=${rule.name}|domain=${rule.domainPattern ?? ""}` +
    `|origin=${rule.originPattern ?? ""}` +
    `|description=${rule.description ?? ""}|level=${rule.level ?? ""}`
  );
}

export interface RuleDiffEntry<T> {
  /** "same" when the key appears in both; "added" / "removed" otherwise. */
  status: "same" | "added" | "removed";
  rule: T;
}

function diffList<T>(
  current: readonly T[] | null | undefined,
  incoming: readonly T[] | null | undefined,
  key: (rule: T) => string,
): RuleDiffEntry<T>[] {
  const currentList = current ?? [];
  const incomingList = incoming ?? [];
  const currentKeys = new Set(currentList.map(key));
  const incomingKeys = new Set(incomingList.map(key));

  const result: RuleDiffEntry<T>[] = [];
  // Emit removals (present in current, absent in incoming) first so the
  // preview reads top-to-bottom as "what's going away, what's staying,
  // what's new".
  for (const rule of currentList) {
    if (!incomingKeys.has(key(rule))) {
      result.push({ status: "removed", rule });
    }
  }
  for (const rule of incomingList) {
    if (currentKeys.has(key(rule))) {
      result.push({ status: "same", rule });
    } else {
      result.push({ status: "added", rule });
    }
  }
  return result;
}

export interface BoundaryDiff {
  opacities: RuleDiffEntry<BoundaryOpacityRuleDto>[];
  projections: RuleDiffEntry<BoundaryProjectionRuleDto>[];
  syntheses: RuleDiffEntry<BoundarySynthesisRuleDto>[];
  /** True when every rule compared is "same" — confirms a no-op upload. */
  isNoOp: boolean;
  /** Count of added rules across all three dimensions. */
  addedCount: number;
  /** Count of removed rules across all three dimensions. */
  removedCount: number;
}

export function diffBoundaries(
  current: UnitBoundaryResponse | null | undefined,
  incoming: UnitBoundaryResponse,
): BoundaryDiff {
  const opacities = diffList(
    current?.opacities,
    incoming.opacities,
    opacityKey,
  );
  const projections = diffList(
    current?.projections,
    incoming.projections,
    projectionKey,
  );
  const syntheses = diffList(
    current?.syntheses,
    incoming.syntheses,
    synthesisKey,
  );

  const all = [...opacities, ...projections, ...syntheses];
  const addedCount = all.filter((e) => e.status === "added").length;
  const removedCount = all.filter((e) => e.status === "removed").length;
  const isNoOp = addedCount === 0 && removedCount === 0;

  return {
    opacities,
    projections,
    syntheses,
    isNoOp,
    addedCount,
    removedCount,
  };
}

// Pretty-printers used by the preview card. Parallels
// `UnitBoundaryCommand.FormatBoundaryForHumans` — keeping the CLI and
// portal summaries visually aligned is an explicit #495 / #524
// acceptance.

export function formatOpacity(rule: BoundaryOpacityRuleDto): string {
  return `domain: ${rule.domainPattern ?? "(any)"} · origin: ${
    rule.originPattern ?? "(any)"
  }`;
}

export function formatProjection(rule: BoundaryProjectionRuleDto): string {
  return (
    `domain: ${rule.domainPattern ?? "(any)"} · origin: ${
      rule.originPattern ?? "(any)"
    } · rename: ${rule.renameTo ?? "(no change)"} · retag: ${
      rule.retag ?? "(no change)"
    } · level: ${rule.overrideLevel ?? "(no change)"}`
  );
}

export function formatSynthesis(rule: BoundarySynthesisRuleDto): string {
  return (
    `name: ${rule.name} · domain: ${rule.domainPattern ?? "(any)"}` +
    ` · origin: ${rule.originPattern ?? "(any)"} · level: ${
      rule.level ?? "(strongest seen)"
    }`
  );
}
