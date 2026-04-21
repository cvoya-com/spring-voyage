import { render, screen } from "@testing-library/react";
import { describe, expect, it, vi } from "vitest";

import type { AgentNode } from "../aggregate";

const useMemoriesMock = vi.fn();
vi.mock("@/lib/api/queries", () => ({
  useMemories: (scope: string, id: string) => useMemoriesMock(scope, id),
}));

import AgentMemoryTab from "./agent-memory";

describe("AgentMemoryTab", () => {
  it("calls useMemories with scope=agent and renders v2.1 empty state", () => {
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
});
