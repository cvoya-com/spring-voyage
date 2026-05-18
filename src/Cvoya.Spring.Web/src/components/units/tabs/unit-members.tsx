"use client";

// Unit Members tab (#2270 / #2427). Renamed from `Unit × Agents` —
// the slot now surfaces every subject that belongs to the unit:
//
//   - Agent members (existing membership rows, unchanged from the
//     pre-rename `agents-tab.tsx`).
//   - Sub-units (read from the active node's children — clicking a
//     sub-unit card drills into that unit's own Members tab).
//   - Human team-role members (new — backed by
//     `GET/POST/PATCH/DELETE /api/v1/tenant/units/{id}/members/humans`,
//     landed under #2409).
//
// The catalog entry in `UNIT_TABS` is `Members`; the file/component
// rename is one-shot per v0.1 aggressive-cleanup convention — no
// `?tab=Agents` redirect. Stale deep-links fall back to Overview via
// the DetailPane's existing invalid-tab effect.

import { MembersTab } from "@/components/units/tab-impls/members-tab";

import { registerTab, type TabContentProps } from "./index";

function UnitMembersTab({ node }: TabContentProps) {
  if (node.kind !== "Unit") return null;
  return (
    <MembersTab
      unitId={node.id}
      unitDisplayName={node.name}
      childNodes={node.children ?? []}
    />
  );
}

registerTab("Unit", "Members", UnitMembersTab);

export default UnitMembersTab;
