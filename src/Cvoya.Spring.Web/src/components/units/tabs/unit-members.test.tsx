import { render, screen } from "@testing-library/react";
import { describe, expect, it, vi } from "vitest";

import type { UnitNode, TreeNode } from "../aggregate";

// Mock the members panel so the test doesn't drag in the full query
// + mutation stack — the tab under test is a thin adapter that
// forwards the unit identity + display name + child nodes to the
// canonical Members panel.
vi.mock("@/components/units/tab-impls/members-tab", () => ({
  MembersTab: ({
    unitId,
    unitDisplayName,
    childNodes,
  }: {
    unitId: string;
    unitDisplayName: string;
    childNodes: readonly TreeNode[];
  }) => (
    <div
      data-testid="members-tab-panel"
      data-unit-id={unitId}
      data-unit-display-name={unitDisplayName}
      data-children-count={String(childNodes.length)}
    />
  ),
}));

import UnitMembersTab from "./unit-members";

describe("UnitMembersTab adapter (#2270 / #2427)", () => {
  it("forwards node id, display name, and children to the canonical MembersTab", () => {
    const child: UnitNode = {
      kind: "Unit",
      id: "engineering/backend",
      name: "Backend",
      status: "running",
    };
    const node: UnitNode = {
      kind: "Unit",
      id: "engineering",
      name: "Engineering",
      status: "running",
      children: [child],
    };
    render(<UnitMembersTab node={node} path={[node]} />);
    const panel = screen.getByTestId("members-tab-panel");
    expect(panel.dataset.unitId).toBe("engineering");
    expect(panel.dataset.unitDisplayName).toBe("Engineering");
    expect(panel.dataset.childrenCount).toBe("1");
  });

  it("returns null for non-Unit nodes so the registry can skip the slot", () => {
    const tenant: TreeNode = {
      kind: "Tenant",
      id: "tenant",
      name: "Tenant",
      status: "running",
    };
    const { container } = render(
      <UnitMembersTab node={tenant} path={[tenant]} />,
    );
    expect(container.firstChild).toBeNull();
  });
});
