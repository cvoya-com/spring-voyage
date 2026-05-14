"use client";

// Unified Messages tab for Unit and Agent subjects (#2256, umbrella
// #2252). Both kinds render the same 1:1 engagement timeline + inline
// composer; the only difference is the address scheme used to filter
// threads and route outbound messages. Tenant is intentionally not a
// subject here: per docs/design/canonical-tabs.md § 1 + § 4, Tenant
// does not participate in threads, so Messages does-not-apply on Tenant
// and is absent from `TENANT_TABS`.
//
// The shared `<UnitAgentMessagesView>` continues to carry the timeline
// + composer body; this component is the canonical kind-parameterised
// entry point that the per-subject `unit-messages.tsx` / `agent-messages.tsx`
// wrappers delegate to. Mirrors the shape of `<ActivityTab>` (#2253 /
// #2259).

import { UnitAgentMessagesView } from "@/components/units/tabs/unit-agent-messages-view";

/**
 * Subjects the unified Messages tab can be driven by. The threads filter
 * keys on `unit:<id>` / `agent:<id>` and the outbound recipient on
 * `{ scheme: "unit"|"agent", path }` — both mirror the kind directly.
 */
export type MessagesSubjectKind = "Unit" | "Agent";

export interface MessagesTabProps {
  /** Subject kind — drives the threads filter and outbound recipient. */
  kind: MessagesSubjectKind;
  /** Stable id of the subject (unit id or agent id). */
  id: string;
  /** Display name for the empty-state copy ("No conversation with <name> yet"). */
  name: string;
}

const SCHEME: Record<MessagesSubjectKind, "unit" | "agent"> = {
  Unit: "unit",
  Agent: "agent",
};

/**
 * Subject-agnostic Messages tab. Renders the most-recently-active thread
 * involving the hosting subject inline plus a persistent composer that
 * creates a fresh thread when none exists yet. See `<UnitAgentMessagesView>`
 * for the body contract.
 */
export function MessagesTab({ kind, id, name }: MessagesTabProps) {
  const scheme = SCHEME[kind];
  return (
    <UnitAgentMessagesView
      targetScheme={scheme}
      targetPath={id}
      targetName={name}
      rootTestId={`tab-${scheme}-messages`}
    />
  );
}
