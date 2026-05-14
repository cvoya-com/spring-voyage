import { render, screen } from "@testing-library/react";
import { describe, expect, it, vi } from "vitest";

import type { AgentNode } from "../aggregate";

vi.mock("@/components/units/tab-impls/activity-tab", () => ({
  ActivityTab: ({ kind, id }: { kind: string; id: string }) => (
    <div data-testid="unified-activity-tab" data-kind={kind} data-id={id} />
  ),
}));

import AgentActivityTab from "./agent-activity";

describe("AgentActivityTab adapter", () => {
  it("forwards node.id to the unified ActivityTab as an Agent subject", () => {
    const node: AgentNode = {
      kind: "Agent",
      id: "ada",
      name: "Ada",
      status: "running",
    };
    render(<AgentActivityTab node={node} path={[node]} />);
    const el = screen.getByTestId("unified-activity-tab");
    expect(el.dataset.kind).toBe("Agent");
    expect(el.dataset.id).toBe("ada");
  });
});
