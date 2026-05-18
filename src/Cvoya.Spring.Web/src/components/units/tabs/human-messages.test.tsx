import { render, screen } from "@testing-library/react";
import { describe, expect, it } from "vitest";

import type { AgentNode, HumanNode } from "../aggregate";

import HumanMessagesTab from "./human-messages";

describe("HumanMessagesTab placeholder (slot reserved for #2268)", () => {
  it("renders the deferred-tab placeholder for a Human node", () => {
    const node: HumanNode = {
      kind: "Human",
      id: "11111111-1111-1111-1111-111111111111",
      name: "savas",
      status: "running",
    };
    render(<HumanMessagesTab node={node} path={[node]} />);
    // TabPlaceholder data-testid is `tab-placeholder-<lowercased tab>`.
    expect(screen.getByTestId("tab-placeholder-messages")).toBeInTheDocument();
  });

  it("renders nothing for a non-Human node — guards against accidental cross-mount", () => {
    const node: AgentNode = {
      kind: "Agent",
      id: "ada",
      name: "Ada",
      status: "running",
    };
    const { container } = render(
      <HumanMessagesTab node={node} path={[node]} />,
    );
    expect(container.firstChild).toBeNull();
  });
});
