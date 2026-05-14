"use client";

// Unit Messages tab (#1459 / #1460; unified under #2256).
// Thin wrapper around the canonical `<MessagesTab>` — same timeline,
// same composer, same address routing shared with the agent surface.
// The unified component accepts a `{ kind, id, name }` triple so both
// subjects render the same control (see docs/design/canonical-tabs.md
// § 5.3).

import { MessagesTab } from "@/components/units/tab-impls/messages-tab";

import { registerTab, type TabContentProps } from "./index";

function UnitMessagesTab({ node }: TabContentProps) {
  if (node.kind !== "Unit") return null;
  return <MessagesTab kind="Unit" id={node.id} name={node.name} />;
}

registerTab("Unit", "Messages", UnitMessagesTab);

export default UnitMessagesTab;
