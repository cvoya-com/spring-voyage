import { render, screen } from "@testing-library/react";
import { describe, expect, it, vi } from "vitest";

import type { AgentNode } from "../aggregate";

const useActivityQueryMock = vi.fn();
vi.mock("@/lib/api/queries", () => ({
  useActivityQuery: (params: unknown) => useActivityQueryMock(params),
}));
vi.mock("@/lib/stream/use-activity-stream", () => ({
  useActivityStream: vi.fn(),
}));

import AgentActivityTab from "./agent-activity";

describe("AgentActivityTab", () => {
  const node: AgentNode = {
    kind: "Agent",
    id: "ada",
    name: "Ada",
    status: "running",
  };

  it("renders the empty state when there are no events", () => {
    useActivityQueryMock.mockReturnValueOnce({
      data: { items: [] },
      isLoading: false,
      isFetching: false,
      error: null,
      refetch: vi.fn(),
    });
    render(<AgentActivityTab node={node} path={[node]} />);
    expect(useActivityQueryMock).toHaveBeenCalledWith({
      source: "agent:ada",
      pageSize: "20",
    });
    expect(screen.getByTestId("tab-agent-activity-empty")).toBeInTheDocument();
  });
});
