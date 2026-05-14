"use client";

// Agent Messages tab (#1459 / #1460; unified under #2256).
// Thin wrapper around the canonical `<MessagesTab>` — same timeline,
// same composer, same address routing shared with the unit surface.
// See `unit-messages.tsx` for the shared rationale and
// docs/design/canonical-tabs.md § 5.3.

import { MessagesTab } from "@/components/units/tab-impls/messages-tab";

import { registerTab, type TabContentProps } from "./index";

function AgentMessagesTab({ node }: TabContentProps) {
  if (node.kind !== "Agent") return null;
  return <MessagesTab kind="Agent" id={node.id} name={node.name} />;
}

registerTab("Agent", "Messages", AgentMessagesTab);

export default AgentMessagesTab;
