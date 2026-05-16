/**
 * Tests for the Agent Skills tab wrapper (#2271, #2354).
 *
 * After #2354 the body is a static placeholder — no API calls.
 */

import { render, screen } from "@testing-library/react";
import { describe, expect, it } from "vitest";

import type { AgentNode, UnitNode } from "../aggregate";

import AgentSkillsTab from "./agent-skills";

const agentNode: AgentNode = {
  kind: "Agent",
  id: "ada",
  name: "Ada",
  status: "running",
};

describe("AgentSkillsTab (wrapper)", () => {
  it("renders the Skills placeholder for an Agent node", () => {
    render(<AgentSkillsTab node={agentNode} path={[agentNode]} />);
    expect(screen.getByTestId("tab-agent-skills")).toBeInTheDocument();
    expect(screen.getByText("Skills coming soon")).toBeInTheDocument();
    expect(screen.getByRole("link", { name: /Config → Tools/i })).toBeInTheDocument();
  });

  it("renders nothing for a non-Agent node (registry-guard)", () => {
    const unitNode: UnitNode = {
      kind: "Unit",
      id: "engineering",
      name: "Engineering",
      status: "running",
    };
    const { container } = render(
      <AgentSkillsTab node={unitNode} path={[unitNode]} />,
    );
    expect(container.firstChild).toBeNull();
  });
});
