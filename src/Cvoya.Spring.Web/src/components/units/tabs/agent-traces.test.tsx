import { render, screen } from "@testing-library/react";
import { describe, expect, it } from "vitest";

import type { AgentNode, UnitNode } from "../aggregate";

import AgentTracesTab from "./agent-traces";

describe("AgentTracesTab (wrapper)", () => {
  it("delegates to TracesTab and renders the V21 fixture", () => {
    const node: AgentNode = {
      kind: "Agent",
      id: "ada",
      name: "Ada",
      status: "running",
    };
    render(<AgentTracesTab node={node} path={[node]} />);
    expect(screen.getByTestId("tab-agent-traces-mock-note")).toHaveTextContent(
      "V21-traces-api",
    );
    expect(screen.getAllByRole("row").length).toBe(7);
  });

  it("renders nothing for a non-Agent node (registry-guard)", () => {
    const unitNode: UnitNode = {
      kind: "Unit",
      id: "engineering",
      name: "Engineering",
      status: "running",
    };
    const { container } = render(
      <AgentTracesTab node={unitNode} path={[unitNode]} />,
    );
    expect(container.firstChild).toBeNull();
  });
});
