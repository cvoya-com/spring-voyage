import { render, screen } from "@testing-library/react";
import { describe, expect, it, vi } from "vitest";

import type { AgentNode } from "../aggregate";

const useActivityQueryMock = vi.fn();
const useAgentCostTimeseriesMock = vi.fn();
const useAgentCostBreakdownMock = vi.fn();
vi.mock("@/lib/api/queries", () => ({
  useActivityQuery: (params: unknown) => useActivityQueryMock(params),
  useAgentCostTimeseries: (id: string, window: string, bucket: string) =>
    useAgentCostTimeseriesMock(id, window, bucket),
  useAgentCostBreakdown: (id: string) => useAgentCostBreakdownMock(id),
}));
vi.mock("@/lib/stream/use-activity-stream", () => ({
  useActivityStream: vi.fn(),
}));

import AgentActivityTab from "./agent-activity";

const node: AgentNode = {
  kind: "Agent",
  id: "ada",
  name: "Ada",
  status: "running",
};

const emptyActivity = {
  data: { items: [] },
  isLoading: false,
  isFetching: false,
  error: null,
  refetch: vi.fn(),
};

const emptyTimeseries = { data: null, isLoading: false };
const emptyBreakdown = { data: null, isLoading: false };

describe("AgentActivityTab", () => {
  it("renders the empty state when there are no events", () => {
    useActivityQueryMock.mockReturnValueOnce(emptyActivity);
    useAgentCostTimeseriesMock.mockReturnValue(emptyTimeseries);
    useAgentCostBreakdownMock.mockReturnValue(emptyBreakdown);
    render(<AgentActivityTab node={node} path={[node]} />);
    expect(useActivityQueryMock).toHaveBeenCalledWith({
      source: "agent:ada",
      pageSize: "20",
    });
    expect(screen.getByTestId("tab-agent-activity-empty")).toBeInTheDocument();
  });

  it("shows the cost timeseries card with empty state when no data", () => {
    useActivityQueryMock.mockReturnValue(emptyActivity);
    useAgentCostTimeseriesMock.mockReturnValue(emptyTimeseries);
    useAgentCostBreakdownMock.mockReturnValue(emptyBreakdown);
    render(<AgentActivityTab node={node} path={[node]} />);
    expect(
      screen.getByTestId("agent-cost-timeseries-card"),
    ).toBeInTheDocument();
    expect(
      screen.getByTestId("agent-cost-timeseries-empty"),
    ).toBeInTheDocument();
  });

  it("renders the sparkline when timeseries data is present", () => {
    useActivityQueryMock.mockReturnValue(emptyActivity);
    useAgentCostTimeseriesMock.mockReturnValue({
      data: {
        scope: "agent",
        id: "ada",
        bucket: "1d",
        from: "2024-01-01T00:00:00Z",
        to: "2024-01-08T00:00:00Z",
        points: [
          { t: "2024-01-01T00:00:00Z", costUsd: 0.5 },
          { t: "2024-01-02T00:00:00Z", costUsd: 1.2 },
          { t: "2024-01-03T00:00:00Z", costUsd: 0.8 },
        ],
      },
      isLoading: false,
    });
    useAgentCostBreakdownMock.mockReturnValue(emptyBreakdown);
    render(<AgentActivityTab node={node} path={[node]} />);
    expect(screen.getByTestId("agent-cost-sparkline")).toBeInTheDocument();
  });

  it("renders the breakdown table when entries are present", () => {
    useActivityQueryMock.mockReturnValue(emptyActivity);
    useAgentCostTimeseriesMock.mockReturnValue(emptyTimeseries);
    useAgentCostBreakdownMock.mockReturnValue({
      data: {
        agentId: "ada",
        from: "2024-01-01T00:00:00Z",
        to: "2024-01-08T00:00:00Z",
        entries: [
          {
            key: "claude-3-5-sonnet",
            kind: "llm",
            totalCost: 1.5,
            recordCount: 10,
          },
          { key: "gpt-4o", kind: "llm", totalCost: 0.3, recordCount: 3 },
        ],
      },
      isLoading: false,
    });
    render(<AgentActivityTab node={node} path={[node]} />);
    expect(
      screen.getByTestId("agent-cost-breakdown-card"),
    ).toBeInTheDocument();
    expect(screen.getByText("claude-3-5-sonnet")).toBeInTheDocument();
    expect(screen.getByText("gpt-4o")).toBeInTheDocument();
  });

  it("hides the breakdown table when entries are empty", () => {
    useActivityQueryMock.mockReturnValue(emptyActivity);
    useAgentCostTimeseriesMock.mockReturnValue(emptyTimeseries);
    useAgentCostBreakdownMock.mockReturnValue({
      data: { agentId: "ada", from: "", to: "", entries: [] },
      isLoading: false,
    });
    render(<AgentActivityTab node={node} path={[node]} />);
    expect(
      screen.queryByTestId("agent-cost-breakdown-card"),
    ).not.toBeInTheDocument();
  });
});
