"use client";

// Human Overview tab (#2267, design: docs/design/canonical-tabs.md
// § 4.1 / § 5.1). Thin wrapper around the unified `<OverviewTab>` —
// the kind-keyed dispatcher inside that component renders the
// human-specific body (profile card + facts grid + caveat copy)
// with the "You" hint when the active human matches the
// currently-authenticated caller.
//
// Memberships are intentionally absent from this slot — no
// `GET /api/v1/tenant/humans/{id}/memberships` endpoint exists in
// v0.1, and aggregating across per-unit endpoints is deferred to
// v0.2 (see PR body for the follow-up issue).

import { OverviewTab } from "@/components/units/tab-impls/overview-tab";

import { registerTab, type TabContentProps } from "./index";

function HumanOverviewTab({ node }: TabContentProps) {
  if (node.kind !== "Human") return null;
  return <OverviewTab kind="Human" node={node} />;
}

registerTab("Human", "Overview", HumanOverviewTab);

export default HumanOverviewTab;
