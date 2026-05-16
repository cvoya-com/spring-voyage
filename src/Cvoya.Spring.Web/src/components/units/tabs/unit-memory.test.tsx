import { render, screen } from "@testing-library/react";
import { beforeEach, describe, expect, it, vi } from "vitest";

import type { UnitNode, AgentNode } from "../aggregate";

const useMemoriesMock = vi.fn();
vi.mock("@/lib/api/queries", () => ({
  useMemories: (
    scope: string,
    id: string,
    options?: Record<string, unknown>,
  ) => useMemoriesMock(scope, id, options),
}));

import UnitMemoryTab from "./unit-memory";

describe("UnitMemoryTab (wrapper)", () => {
  beforeEach(() => {
    useMemoriesMock.mockReset();
  });

  it("delegates to the unified MemoryTab with scope=unit", () => {
    useMemoriesMock.mockReturnValue({
      data: { shortTerm: [], longTerm: [] },
      isLoading: false,
      error: null,
    });
    const node: UnitNode = {
      kind: "Unit",
      id: "engineering",
      name: "Engineering",
      status: "running",
    };
    render(<UnitMemoryTab node={node} path={[node]} />);
    expect(useMemoriesMock).toHaveBeenCalledWith(
      "unit",
      "engineering",
      expect.objectContaining({}),
    );
    expect(screen.getByTestId("tab-unit-memory-empty")).toBeInTheDocument();
  });

  it("renders nothing for a non-Unit node (registry-guard)", () => {
    const agentNode: AgentNode = {
      kind: "Agent",
      id: "ada",
      name: "Ada",
      status: "running",
    };
    const { container } = render(
      <UnitMemoryTab node={agentNode} path={[agentNode]} />,
    );
    expect(container.firstChild).toBeNull();
    expect(useMemoriesMock).not.toHaveBeenCalled();
  });
});
