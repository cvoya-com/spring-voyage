import { render, screen } from "@testing-library/react";
import { describe, expect, it, vi } from "vitest";

import type { AgentNode, TenantNode } from "../aggregate";

vi.mock("@/components/units/tab-impls/overview-tab", () => ({
  OverviewTab: ({ kind, node }: { kind: string; node: { id: string } }) => (
    <div
      data-testid="unified-overview-tab"
      data-kind={kind}
      data-id={node.id}
    />
  ),
}));

import TenantOverviewTab from "./tenant-overview";

describe("TenantOverviewTab adapter", () => {
  it("forwards the node to the unified OverviewTab as a Tenant subject", () => {
    const node: TenantNode = {
      kind: "Tenant",
      id: "tenant",
      name: "Tenant",
      status: "running",
      children: [],
    };
    render(<TenantOverviewTab node={node} path={[node]} />);
    const el = screen.getByTestId("unified-overview-tab");
    expect(el.dataset.kind).toBe("Tenant");
    expect(el.dataset.id).toBe("tenant");
  });

  it("renders nothing when invoked with a non-Tenant node", () => {
    const node: AgentNode = {
      kind: "Agent",
      id: "ada",
      name: "Ada",
      status: "running",
    };
    const { container } = render(
      <TenantOverviewTab node={node} path={[node]} />,
    );
    expect(container.firstChild).toBeNull();
  });
});
