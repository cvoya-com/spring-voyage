import { render, screen } from "@testing-library/react";
import { describe, expect, it } from "vitest";

import type { AgentNode, HumanNode } from "../aggregate";

import HumanConfigTab from "./human-config";

describe("HumanConfigTab placeholder (slot reserved for #2269)", () => {
  it("renders the deferred-tab placeholder for a Human node", () => {
    const node: HumanNode = {
      kind: "Human",
      id: "11111111-1111-1111-1111-111111111111",
      name: "savas",
      status: "running",
    };
    render(<HumanConfigTab node={node} path={[node]} />);
    expect(screen.getByTestId("tab-placeholder-config")).toBeInTheDocument();
  });

  it("renders nothing for a non-Human node", () => {
    const node: AgentNode = {
      kind: "Agent",
      id: "ada",
      name: "Ada",
      status: "running",
    };
    const { container } = render(
      <HumanConfigTab node={node} path={[node]} />,
    );
    expect(container.firstChild).toBeNull();
  });
});
