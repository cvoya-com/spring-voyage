/**
 * Tests for the Unit Skills tab wrapper (#2271, #2354).
 *
 * After #2354 the body is a static placeholder — no API calls.
 */

import { render, screen } from "@testing-library/react";
import { describe, expect, it } from "vitest";

import type { AgentNode, UnitNode } from "../aggregate";

import UnitSkillsTab from "./unit-skills";

const unitNode: UnitNode = {
  kind: "Unit",
  id: "engineering",
  name: "Engineering",
  status: "running",
};

describe("UnitSkillsTab (wrapper)", () => {
  it("renders the Skills placeholder for a Unit node", () => {
    render(<UnitSkillsTab node={unitNode} path={[unitNode]} />);
    expect(screen.getByTestId("tab-unit-skills")).toBeInTheDocument();
    expect(screen.getByText("Skills coming soon")).toBeInTheDocument();
    expect(screen.getByRole("link", { name: /Config → Tools/i })).toBeInTheDocument();
  });

  it("renders nothing for a non-Unit node (registry-guard)", () => {
    const agentNode: AgentNode = {
      kind: "Agent",
      id: "ada",
      name: "Ada",
      status: "running",
    };
    const { container } = render(
      <UnitSkillsTab node={agentNode} path={[agentNode]} />,
    );
    expect(container.firstChild).toBeNull();
  });
});
