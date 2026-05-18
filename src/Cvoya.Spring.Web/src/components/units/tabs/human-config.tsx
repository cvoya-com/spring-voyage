"use client";

// Human Config tab slot (#2269). The slot is reserved here so the
// completeness check in `register-all.test.ts` passes against the
// HUMAN_TABS catalog, and so a stale URL pointing at the slot does
// not crash the Detail Pane. The Identity + Connector sub-tabs ship
// in Portal Wave B's follow-up PR; until then the placeholder copy
// makes the deferred scope explicit.

import { TabPlaceholder } from "@/components/units/tab-placeholder";

import { registerTab, type TabContentProps } from "./index";

function HumanConfigTab({ node }: TabContentProps) {
  if (node.kind !== "Human") return null;
  return (
    <TabPlaceholder tab="Config" kind="Human">
      The Config tab body for humans ships in the Portal Wave B
      follow-up (#2269). It will surface Identity + Connector sub-tabs
      — personal info and the inbound-routing binding (Slack handle,
      GitHub handle, email) the platform uses to deliver messages
      addressed to <code>human:</code> via the right channel.
    </TabPlaceholder>
  );
}

registerTab("Human", "Config", HumanConfigTab);

export default HumanConfigTab;
