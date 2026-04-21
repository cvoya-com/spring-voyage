import { render, screen } from "@testing-library/react";
import { describe, expect, it, vi } from "vitest";

import type { UnitNode } from "../aggregate";

vi.mock("@/app/units/[id]/orchestration-tab", () => ({
  OrchestrationTab: ({ unitId }: { unitId: string }) => (
    <div data-testid="legacy-orchestration-tab" data-unit-id={unitId} />
  ),
}));

import UnitOrchestrationTab from "./unit-orchestration";

describe("UnitOrchestrationTab adapter", () => {
  it("forwards node.id to the legacy OrchestrationTab", () => {
    const node: UnitNode = {
      kind: "Unit",
      id: "engineering",
      name: "Engineering",
      status: "running",
    };
    render(<UnitOrchestrationTab node={node} path={[node]} />);
    expect(
      screen.getByTestId("legacy-orchestration-tab").dataset.unitId,
    ).toBe("engineering");
  });
});
