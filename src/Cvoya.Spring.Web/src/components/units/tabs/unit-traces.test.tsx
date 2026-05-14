import { render, screen } from "@testing-library/react";
import { describe, expect, it } from "vitest";

import type { AgentNode, UnitNode } from "../aggregate";

import UnitTracesTab from "./unit-traces";

describe("UnitTracesTab (wrapper)", () => {
  it("delegates to TracesTab and renders the V21 fixture for a Unit node", () => {
    const node: UnitNode = {
      kind: "Unit",
      id: "engineering",
      name: "Engineering",
      status: "running",
    };
    render(<UnitTracesTab node={node} path={[node]} />);
    expect(screen.getByTestId("tab-unit-traces-mock-note")).toHaveTextContent(
      "V21-traces-api",
    );
    expect(screen.getAllByRole("row").length).toBe(7);
  });

  it("renders nothing for a non-Unit node (registry-guard)", () => {
    const agentNode: AgentNode = {
      kind: "Agent",
      id: "ada",
      name: "Ada",
      status: "running",
    };
    const { container } = render(
      <UnitTracesTab node={agentNode} path={[agentNode]} />,
    );
    expect(container.firstChild).toBeNull();
  });
});
