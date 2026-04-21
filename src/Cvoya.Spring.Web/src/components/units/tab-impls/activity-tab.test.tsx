import { render, screen, waitFor } from "@testing-library/react";
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { describe, expect, it, vi, beforeEach } from "vitest";
import type { ReactNode } from "react";

import { ActivityTab } from "./activity-tab";

const mockQueryActivity = vi.fn();

vi.mock("@/lib/api/client", () => ({
  api: {
    queryActivity: (...args: unknown[]) => mockQueryActivity(...args),
  },
}));

// The SSE hook would try to open a real EventSource during tests. Stub
// it out — the tests cover the REST-backed query layer here.
vi.mock("@/lib/stream/use-activity-stream", () => ({
  useActivityStream: () => ({ events: [], connected: false }),
}));

const mockResult = {
  items: [
    {
      id: "evt-1",
      source: "unit://eng-team",
      eventType: "StateChanged",
      severity: "Info",
      summary: "Unit started",
      correlationId: null,
      cost: null,
      timestamp: new Date().toISOString(),
    },
  ],
  totalCount: 1,
  page: 1,
  pageSize: 20,
};

function Wrapper({ children }: { children: ReactNode }) {
  const client = new QueryClient({
    defaultOptions: {
      queries: { retry: false, gcTime: 0, staleTime: 0 },
    },
  });
  return <QueryClientProvider client={client}>{children}</QueryClientProvider>;
}

describe("ActivityTab", () => {
  beforeEach(() => {
    mockQueryActivity.mockReset();
    mockQueryActivity.mockResolvedValue(mockResult);
  });

  it("calls API with unit source filter", async () => {
    render(
      <Wrapper>
        <ActivityTab unitId="eng-team" />
      </Wrapper>,
    );
    await waitFor(() => {
      expect(mockQueryActivity).toHaveBeenCalledWith({
        source: "unit:eng-team",
        pageSize: "20",
      });
    });
  });

  it("renders activity events for the unit", async () => {
    render(
      <Wrapper>
        <ActivityTab unitId="eng-team" />
      </Wrapper>,
    );
    await waitFor(() => {
      expect(screen.getByText("Unit started")).toBeInTheDocument();
    });
  });

  it("shows empty message when no events", async () => {
    mockQueryActivity.mockResolvedValue({
      items: [],
      totalCount: 0,
      page: 1,
      pageSize: 20,
    });
    render(
      <Wrapper>
        <ActivityTab unitId="eng-team" />
      </Wrapper>,
    );
    await waitFor(() => {
      expect(
        screen.getByText("No activity events for this unit."),
      ).toBeInTheDocument();
    });
  });
});
