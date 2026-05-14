import { render, screen } from "@testing-library/react";
import { beforeEach, describe, expect, it, vi } from "vitest";

import type { AgentNode, UnitNode } from "../aggregate";

const useMemoriesMock = vi.fn();
vi.mock("@/lib/api/queries", () => ({
  useMemories: (scope: string, id: string) => useMemoriesMock(scope, id),
}));

import AgentMemoryTab from "./agent-memory";

describe("AgentMemoryTab (wrapper)", () => {
  beforeEach(() => {
    useMemoriesMock.mockReset();
  });

  it("delegates to the unified MemoryTab with scope=agent", () => {
    useMemoriesMock.mockReturnValueOnce({
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
    expect(useMemoriesMock).toHaveBeenCalledWith("agent", "ada");
    expect(screen.getByTestId("tab-agent-memory-empty")).toHaveTextContent(
      "v2.1",
    );
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
