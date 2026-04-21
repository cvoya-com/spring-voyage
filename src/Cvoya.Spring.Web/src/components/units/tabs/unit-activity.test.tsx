import { render, screen } from "@testing-library/react";
import { describe, expect, it, vi } from "vitest";

import type { UnitNode } from "../aggregate";

vi.mock("@/app/units/[id]/activity-tab", () => ({
  ActivityTab: ({ unitId }: { unitId: string }) => (
    <div data-testid="legacy-activity-tab" data-unit-id={unitId} />
  ),
}));

import UnitActivityTab from "./unit-activity";

describe("UnitActivityTab adapter", () => {
  it("forwards node.id to the legacy ActivityTab", () => {
    const node: UnitNode = {
      kind: "Unit",
      id: "engineering",
      name: "Engineering",
      status: "running",
    };
    render(<UnitActivityTab node={node} path={[node]} />);
    expect(screen.getByTestId("legacy-activity-tab").dataset.unitId).toBe(
      "engineering",
    );
  });
});
