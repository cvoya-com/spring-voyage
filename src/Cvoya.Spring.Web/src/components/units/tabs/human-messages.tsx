"use client";

// Human Messages tab slot (#2268). The slot is reserved here so the
// completeness check in `register-all.test.ts` passes against the
// HUMAN_TABS catalog, and so a stale URL pointing at the slot does
// not crash the Detail Pane. The actual timeline body — filtered to
// threads the human is addressed in — ships in Portal Wave B's
// follow-up PR.

import { TabPlaceholder } from "@/components/units/tab-placeholder";

import { registerTab, type TabContentProps } from "./index";

function HumanMessagesTab({ node }: TabContentProps) {
  if (node.kind !== "Human") return null;
  return (
    <TabPlaceholder tab="Messages" kind="Human">
      The Messages tab body for humans ships in the Portal Wave B
      follow-up (#2268). The human will see threads they participate in,
      filtered by the <code>human:</code> address scheme.
    </TabPlaceholder>
  );
}

registerTab("Human", "Messages", HumanMessagesTab);

export default HumanMessagesTab;
