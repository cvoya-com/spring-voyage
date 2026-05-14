import { render, screen } from "@testing-library/react";
import { describe, expect, it, vi } from "vitest";

import type { UnitNode } from "../aggregate";

vi.mock("@/components/units/tab-impls/activity-tab", () => ({
  ActivityTab: ({ kind, id }: { kind: string; id: string }) => (
    <div data-testid="unified-activity-tab" data-kind={kind} data-id={id} />
  ),
}));

import UnitActivityTab from "./unit-activity";

describe("UnitActivityTab adapter", () => {
  it("forwards node.id to the unified ActivityTab as a Unit subject", () => {
    const node: UnitNode = {
      kind: "Unit",
      id: "engineering",
      name: "Engineering",
      status: "running",
    };
    render(<UnitActivityTab node={node} path={[node]} />);
    const el = screen.getByTestId("unified-activity-tab");
    expect(el.dataset.kind).toBe("Unit");
    expect(el.dataset.id).toBe("engineering");
  });
});
