import { render, screen } from "@testing-library/react";
import { describe, expect, it } from "vitest";

import type { AgentNode } from "../aggregate";

import AgentTracesTab from "./agent-traces";

describe("AgentTracesTab", () => {
  it("renders the V21-traces-api mock note and a table of placeholder rows", () => {
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
    // 6 mock rows per the fixture.
    const rows = screen.getAllByRole("row");
    // 1 header + 6 body rows = 7.
    expect(rows.length).toBe(7);
  });
});
