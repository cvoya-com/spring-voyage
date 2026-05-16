import { render, screen } from "@testing-library/react";
import { beforeEach, describe, expect, it, vi } from "vitest";

import type { AgentNode, UnitNode } from "../aggregate";

const useMemoriesMock = vi.fn();
vi.mock("@/lib/api/queries", () => ({
  useMemories: (
    scope: string,
    id: string,
    options?: Record<string, unknown>,
  ) => useMemoriesMock(scope, id, options),
}));

import AgentMemoryTab from "./agent-memory";

describe("AgentMemoryTab (wrapper)", () => {
  beforeEach(() => {
    useMemoriesMock.mockReset();
  });

  it("delegates to the unified MemoryTab with scope=agent", () => {
    useMemoriesMock.mockReturnValue({
      data: { shortTerm: [], longTerm: [] },
      isLoading: false,
      error: null,
    });
    const node: AgentNode = {
      kind: "Agent",
      id: "ada",
      name: "Ada",
      status: "running",
    };
    render(<AgentMemoryTab node={node} path={[node]} />);
    expect(useMemoriesMock).toHaveBeenCalledWith(
      "agent",
      "ada",
      expect.objectContaining({}),
    );
    expect(screen.getByTestId("tab-agent-memory-empty")).toBeInTheDocument();
  });

  it("renders nothing for a non-Agent node (registry-guard)", () => {
    const unitNode: UnitNode = {
      kind: "Unit",
      id: "engineering",
      name: "Engineering",
      status: "running",
    };
    const { container } = render(
      <AgentMemoryTab node={unitNode} path={[unitNode]} />,
    );
    expect(container.firstChild).toBeNull();
    expect(useMemoriesMock).not.toHaveBeenCalled();
  });
});
