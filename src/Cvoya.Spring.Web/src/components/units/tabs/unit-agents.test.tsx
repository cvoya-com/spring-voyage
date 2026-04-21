import { render, screen } from "@testing-library/react";
import { describe, expect, it, vi } from "vitest";

import type { UnitNode } from "../aggregate";

// Mock the legacy panel so the test doesn't drag in the full query
// + mutation stack — the tab under test is a thin adapter that
// forwards `node.id` to the legacy component.
vi.mock("@/components/units/tab-impls/agents-tab", () => ({
  AgentsTab: ({ unitId }: { unitId: string }) => (
    <div data-testid="legacy-agents-tab" data-unit-id={unitId} />
  ),
}));

import UnitAgentsTab from "./unit-agents";

describe("UnitAgentsTab adapter", () => {
  it("forwards node.id to the legacy AgentsTab", () => {
    const node: UnitNode = {
      kind: "Unit",
      id: "engineering",
      name: "Engineering",
      status: "running",
    };
    render(<UnitAgentsTab node={node} path={[node]} />);
    const legacy = screen.getByTestId("legacy-agents-tab");
    expect(legacy.dataset.unitId).toBe("engineering");
  });
});
