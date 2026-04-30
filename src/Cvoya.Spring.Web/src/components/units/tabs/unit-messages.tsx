"use client";

// Unit Messages tab (#1459 / #1460).
//
// Renders the full timeline of the {current human, unit} 1:1 engagement
// — every event (messages, tool calls, lifecycle transitions) inline,
// no master/detail split — plus a persistent inline composer at the
// bottom. Sending a message creates the thread implicitly when none
// exists yet.
//
// The shared `<UnitAgentMessagesView>` carries all of the fetch +
// composer logic; this file only narrows the node kind and wires the
// hosting node's address into the view.

import type { TabContentProps } from "./index";
import { registerTab } from "./index";
import { UnitAgentMessagesView } from "./unit-agent-messages-view";

function UnitMessagesTab({ node }: TabContentProps) {
  if (node.kind !== "Unit") return null;
  return (
    <UnitAgentMessagesView
      targetScheme="unit"
      targetPath={node.id}
      targetName={node.name}
      rootTestId="tab-unit-messages"
    />
  );
}

registerTab("Unit", "Messages", UnitMessagesTab);

export default UnitMessagesTab;
