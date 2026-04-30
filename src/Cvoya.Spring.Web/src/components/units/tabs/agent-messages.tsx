"use client";

// Agent Messages tab (#1459 / #1460).
//
// Renders the full timeline of the {current human, agent} 1:1
// engagement inline, with a persistent composer at the bottom.
// See `unit-messages.tsx` for the shared rationale.

import type { TabContentProps } from "./index";
import { registerTab } from "./index";
import { UnitAgentMessagesView } from "./unit-agent-messages-view";

function AgentMessagesTab({ node }: TabContentProps) {
  if (node.kind !== "Agent") return null;
  return (
    <UnitAgentMessagesView
      targetScheme="agent"
      targetPath={node.id}
      targetName={node.name}
      rootTestId="tab-agent-messages"
    />
  );
}

registerTab("Agent", "Messages", AgentMessagesTab);

export default AgentMessagesTab;
