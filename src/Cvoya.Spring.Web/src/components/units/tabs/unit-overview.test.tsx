import { render, screen } from "@testing-library/react";
import { describe, expect, it } from "vitest";

import type { UnitNode } from "../aggregate";

import UnitOverviewTab from "./unit-overview";

describe("UnitOverviewTab", () => {
  it("renders subtree stat tiles rolled up from the node", () => {
    const node: UnitNode = {
      kind: "Unit",
      id: "engineering",
      name: "Engineering",
      status: "running",
      cost24h: 2.5,
      msgs24h: 42,
      children: [
        {
          kind: "Agent",
          id: "ada",
          name: "Ada",
          status: "running",
          cost24h: 1.25,
          msgs24h: 10,
        },
      ],
    };
    render(<UnitOverviewTab node={node} path={[node]} />);
    expect(screen.getByTestId("tab-unit-overview")).toBeInTheDocument();
    // Messages tile shows the rolled-up total (42 + 10 = 52).
    expect(screen.getByText("52")).toBeInTheDocument();
    // The "Cost (24h)" stat tile renders the aggregated cost.
    expect(screen.getByText("Cost (24h)")).toBeInTheDocument();
  });
});
