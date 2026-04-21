import { render, screen } from "@testing-library/react";
import { describe, expect, it, vi } from "vitest";

import type { UnitNode } from "../aggregate";

vi.mock("@/app/units/[id]/policies-tab", () => ({
  PoliciesTab: ({ unitId }: { unitId: string }) => (
    <div data-testid="legacy-policies-tab" data-unit-id={unitId} />
  ),
}));

import UnitPoliciesTab from "./unit-policies";

describe("UnitPoliciesTab adapter", () => {
  it("forwards node.id to the legacy PoliciesTab", () => {
    const node: UnitNode = {
      kind: "Unit",
      id: "engineering",
      name: "Engineering",
      status: "running",
    };
    render(<UnitPoliciesTab node={node} path={[node]} />);
    expect(screen.getByTestId("legacy-policies-tab").dataset.unitId).toBe(
      "engineering",
    );
  });
});
