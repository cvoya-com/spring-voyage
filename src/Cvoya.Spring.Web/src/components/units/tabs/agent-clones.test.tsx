import { render, screen } from "@testing-library/react";
import { describe, expect, it, vi } from "vitest";

import type { AgentNode } from "../aggregate";

const useAgentClonesMock = vi.fn();
vi.mock("@/lib/api/queries", () => ({
  useAgentClones: (id: string) => useAgentClonesMock(id),
}));

import AgentClonesTab from "./agent-clones";

describe("AgentClonesTab", () => {
  const node: AgentNode = {
    kind: "Agent",
    id: "ada",
    name: "Ada",
    status: "running",
  };

  it("renders the empty state when no clones", () => {
    useAgentClonesMock.mockReturnValueOnce({
      data: [],
      isLoading: false,
      error: null,
    });
    render(<AgentClonesTab node={node} path={[node]} />);
    expect(useAgentClonesMock).toHaveBeenCalledWith("ada");
    expect(screen.getByTestId("tab-agent-clones-empty")).toBeInTheDocument();
  });

  it("renders the clone list when there are rows", () => {
    useAgentClonesMock.mockReturnValueOnce({
      data: [
        {
          cloneId: "ada-clone-1",
          parentAgentId: "ada",
          cloneType: "ephemeral-no-memory",
          attachmentMode: "detached",
          status: "active",
          createdAt: "2026-04-20T00:00:00Z",
        },
      ],
      isLoading: false,
      error: null,
    });
    render(<AgentClonesTab node={node} path={[node]} />);
    expect(screen.getByText("ada-clone-1")).toBeInTheDocument();
  });
});
