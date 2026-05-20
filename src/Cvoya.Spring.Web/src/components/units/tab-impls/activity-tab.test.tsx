import { fireEvent, render, screen, waitFor } from "@testing-library/react";
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { describe, expect, it, vi, beforeEach } from "vitest";
import type { ReactNode } from "react";

import { ActivityTab } from "./activity-tab";

const mockQueryActivity = vi.fn();
const mockGetAgentCostTimeseries = vi.fn();
const mockGetAgentCostBreakdown = vi.fn();
// #2564: the runtime-aware OTLP hint reads the subject's effective
// runtime via the execution endpoints (and the agent record for the
// owning-unit fallback).
const mockGetUnitExecution = vi.fn();
const mockGetAgentExecution = vi.fn();
const mockGetAgent = vi.fn();
const mockRouterReplace = vi.fn();

vi.mock("@/lib/api/client", () => ({
  api: {
    queryActivity: (...args: unknown[]) => mockQueryActivity(...args),
    getAgentCostTimeseries: (...args: unknown[]) =>
      mockGetAgentCostTimeseries(...args),
    getAgentCostBreakdown: (...args: unknown[]) =>
      mockGetAgentCostBreakdown(...args),
    getUnitExecution: (...args: unknown[]) => mockGetUnitExecution(...args),
    getAgentExecution: (...args: unknown[]) => mockGetAgentExecution(...args),
    getAgent: (...args: unknown[]) => mockGetAgent(...args),
  },
}));

// The SSE hook would try to open a real EventSource during tests. Stub
// it out — the tests cover the REST-backed query layer here.
vi.mock("@/lib/stream/use-activity-stream", () => ({
  useActivityStream: () => ({ events: [], connected: false }),
}));

// #2502: the activity tab reads filter state from URL search params via
// next/navigation. Stub the hooks so tests can inspect router writes
// and prime the filter state.
vi.mock("next/navigation", () => ({
  useRouter: () => ({ replace: mockRouterReplace, push: vi.fn() }),
  usePathname: () => "/explorer/units/eng-team",
  useSearchParams: () => new URLSearchParams(""),
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

// #2564: a `spring-voyage` execution block — the only OTLP-emitting
// runtime in v0.1, so the no-OTLP hint must stay hidden for it.
const otlpRuntimeExecution = { runtime: "spring-voyage" };
// A `claude-code` execution block — no OTLP, so the hint must render.
const noOtlpRuntimeExecution = { runtime: "claude-code" };

// #2564: minimal `AgentDetailResponse`-shaped record — the runtime hook
// reads `agent.parentUnitId` for the owning-unit fallback.
function agentDetail(parentUnitId: string | null) {
  return { agent: { id: "ada", parentUnitId }, status: null };
}

describe("ActivityTab (Unit subject)", () => {
  beforeEach(() => {
    mockQueryActivity.mockReset();
    mockQueryActivity.mockResolvedValue(mockResult);
    mockGetAgentCostTimeseries.mockReset();
    mockGetAgentCostBreakdown.mockReset();
    mockGetUnitExecution.mockReset();
    mockGetUnitExecution.mockResolvedValue(otlpRuntimeExecution);
    mockGetAgentExecution.mockReset();
    mockGetAgent.mockReset();
  });

  it("calls API with unit source filter", async () => {
    render(
      <Wrapper>
        <ActivityTab kind="Unit" id="eng-team" />
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
        <ActivityTab kind="Unit" id="eng-team" />
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
        <ActivityTab kind="Unit" id="eng-team" />
      </Wrapper>,
    );
    await waitFor(() => {
      expect(
        screen.getByText("No activity events for this unit."),
      ).toBeInTheDocument();
    });
  });

  // #1665: rows that carry a non-empty `details` payload render an
  // expand/collapse toggle that reveals the full structured payload
  // inline. Rows without details render no toggle so the gutter stays
  // tidy. The validation-failure StateChanged row is the canonical
  // example — promoted from Debug to Warning and carrying the
  // validation code/message in details.
  it("renders an expand toggle for events with details and reveals payload on click", async () => {
    mockQueryActivity.mockResolvedValue({
      items: [
        {
          id: "evt-with-details",
          source: "unit://eng-team",
          eventType: "StateChanged",
          severity: "Warning",
          summary:
            "Unit transitioned from Validating to Error: ConfigurationIncomplete — No execution defaults are configured",
          correlationId: null,
          cost: null,
          timestamp: new Date().toISOString(),
          details: {
            action: "StatusTransition",
            from: "Validating",
            to: "Error",
            validationCode: "ConfigurationIncomplete",
            validationMessage: "No execution defaults are configured",
            error: {
              Step: "SchedulingWorkflow",
              Code: "ConfigurationIncomplete",
              Message: "No execution defaults are configured",
              Details: { missing: "image,runtime" },
            },
          },
        },
      ],
      totalCount: 1,
      page: 1,
      pageSize: 20,
    });

    render(
      <Wrapper>
        <ActivityTab kind="Unit" id="eng-team" />
      </Wrapper>,
    );

    const toggle = await screen.findByTestId("activity-row-toggle");
    expect(toggle).toHaveAttribute("aria-expanded", "false");
    expect(screen.queryByTestId("activity-row-details")).not.toBeInTheDocument();

    fireEvent.click(toggle);

    expect(toggle).toHaveAttribute("aria-expanded", "true");
    const details = await screen.findByTestId("activity-row-details");
    expect(details).toHaveTextContent('"validationCode": "ConfigurationIncomplete"');
    expect(details).toHaveTextContent('"missing": "image,runtime"');

    fireEvent.click(toggle);
    expect(toggle).toHaveAttribute("aria-expanded", "false");
    expect(screen.queryByTestId("activity-row-details")).not.toBeInTheDocument();
  });

  it("does not render a toggle for events without details", async () => {
    mockQueryActivity.mockResolvedValue({
      items: [
        {
          id: "evt-bare",
          source: "unit://eng-team",
          eventType: "MessageReceived",
          severity: "Info",
          summary: "Bare row",
          correlationId: null,
          cost: null,
          timestamp: new Date().toISOString(),
        },
      ],
      totalCount: 1,
      page: 1,
      pageSize: 20,
    });
    render(
      <Wrapper>
        <ActivityTab kind="Unit" id="eng-team" />
      </Wrapper>,
    );
    await waitFor(() => {
      expect(screen.getByText("Bare row")).toBeInTheDocument();
    });
    expect(screen.queryByTestId("activity-row-toggle")).not.toBeInTheDocument();
  });

  it("does not render agent cost cards for the Unit subject", async () => {
    render(
      <Wrapper>
        <ActivityTab kind="Unit" id="eng-team" />
      </Wrapper>,
    );
    await waitFor(() => {
      expect(screen.getByText("Unit started")).toBeInTheDocument();
    });
    expect(
      screen.queryByTestId("agent-cost-timeseries-card"),
    ).not.toBeInTheDocument();
    expect(
      screen.queryByTestId("agent-cost-breakdown-card"),
    ).not.toBeInTheDocument();
    // The unit Activity tab must not call the agent cost endpoints —
    // they would 404 against a unit id.
    expect(mockGetAgentCostTimeseries).not.toHaveBeenCalled();
    expect(mockGetAgentCostBreakdown).not.toHaveBeenCalled();
  });
});

describe("ActivityTab (Agent subject)", () => {
  beforeEach(() => {
    mockQueryActivity.mockReset();
    mockQueryActivity.mockResolvedValue({
      items: [],
      totalCount: 0,
      page: 1,
      pageSize: 20,
    });
    mockGetAgentCostTimeseries.mockReset();
    mockGetAgentCostTimeseries.mockResolvedValue(null);
    mockGetAgentCostBreakdown.mockReset();
    mockGetAgentCostBreakdown.mockResolvedValue(null);
    mockGetUnitExecution.mockReset();
    mockGetUnitExecution.mockResolvedValue(otlpRuntimeExecution);
    mockGetAgentExecution.mockReset();
    mockGetAgentExecution.mockResolvedValue(otlpRuntimeExecution);
    mockGetAgent.mockReset();
    mockGetAgent.mockResolvedValue(agentDetail(null));
  });

  it("calls API with agent source filter", async () => {
    render(
      <Wrapper>
        <ActivityTab kind="Agent" id="ada" />
      </Wrapper>,
    );
    await waitFor(() => {
      expect(mockQueryActivity).toHaveBeenCalledWith({
        source: "agent:ada",
        pageSize: "20",
      });
    });
  });

  it("shows the agent-flavoured empty message when no events", async () => {
    render(
      <Wrapper>
        <ActivityTab kind="Agent" id="ada" />
      </Wrapper>,
    );
    await waitFor(() => {
      expect(
        screen.getByText("No activity for this agent yet."),
      ).toBeInTheDocument();
    });
  });

  it("renders the cost timeseries card (empty state when no data)", async () => {
    render(
      <Wrapper>
        <ActivityTab kind="Agent" id="ada" />
      </Wrapper>,
    );
    await waitFor(() => {
      expect(
        screen.getByTestId("agent-cost-timeseries-card"),
      ).toBeInTheDocument();
    });
    await waitFor(() => {
      expect(
        screen.getByTestId("agent-cost-timeseries-empty"),
      ).toBeInTheDocument();
    });
  });

  it("renders the sparkline when timeseries data is present", async () => {
    mockGetAgentCostTimeseries.mockResolvedValue({
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
    });
    render(
      <Wrapper>
        <ActivityTab kind="Agent" id="ada" />
      </Wrapper>,
    );
    await waitFor(() => {
      expect(screen.getByTestId("agent-cost-sparkline")).toBeInTheDocument();
    });
  });

  it("renders the breakdown table when entries are present", async () => {
    mockGetAgentCostBreakdown.mockResolvedValue({
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
    });
    render(
      <Wrapper>
        <ActivityTab kind="Agent" id="ada" />
      </Wrapper>,
    );
    await waitFor(() => {
      expect(
        screen.getByTestId("agent-cost-breakdown-card"),
      ).toBeInTheDocument();
    });
    expect(screen.getByText("claude-3-5-sonnet")).toBeInTheDocument();
    expect(screen.getByText("gpt-4o")).toBeInTheDocument();
  });

  it("hides the breakdown table when entries are empty", async () => {
    mockGetAgentCostBreakdown.mockResolvedValue({
      agentId: "ada",
      from: "",
      to: "",
      entries: [],
    });
    render(
      <Wrapper>
        <ActivityTab kind="Agent" id="ada" />
      </Wrapper>,
    );
    // Wait for the timeseries card so React-Query has settled before
    // we assert the absence of the breakdown card.
    await waitFor(() => {
      expect(
        screen.getByTestId("agent-cost-timeseries-card"),
      ).toBeInTheDocument();
    });
    expect(
      screen.queryByTestId("agent-cost-breakdown-card"),
    ).not.toBeInTheDocument();
  });

  it("renders the expandable-row affordance for agent events with details", async () => {
    mockQueryActivity.mockResolvedValue({
      items: [
        {
          id: "agent-evt-with-details",
          source: "agent://ada",
          eventType: "StateChanged",
          severity: "Warning",
          summary: "Agent paused: BudgetExceeded",
          correlationId: null,
          cost: null,
          timestamp: new Date().toISOString(),
          details: {
            action: "StatusTransition",
            from: "Running",
            to: "Paused",
            reasonCode: "BudgetExceeded",
            reasonMessage: "Daily budget cap reached",
          },
        },
      ],
      totalCount: 1,
      page: 1,
      pageSize: 20,
    });

    render(
      <Wrapper>
        <ActivityTab kind="Agent" id="ada" />
      </Wrapper>,
    );

    const toggle = await screen.findByTestId("activity-row-toggle");
    expect(toggle).toHaveAttribute("aria-expanded", "false");

    fireEvent.click(toggle);

    const details = await screen.findByTestId("activity-row-details");
    expect(details).toHaveTextContent('"reasonCode": "BudgetExceeded"');
  });
});

// #2502: filter chips above the unit/agent activity feed.
describe("ActivityTab filter chips (#2502)", () => {
  beforeEach(() => {
    mockQueryActivity.mockReset();
    mockRouterReplace.mockReset();
    mockQueryActivity.mockResolvedValue({
      items: [
        {
          id: "evt-llm",
          source: "unit://eng-team",
          eventType: "LlmTurn",
          severity: "Info",
          summary: "Turn alpha",
          correlationId: "thread-1",
          messageId: "msg-1",
          cost: null,
          timestamp: new Date().toISOString(),
        },
        {
          id: "evt-log",
          source: "unit://eng-team",
          eventType: "RuntimeLog",
          severity: "Info",
          summary: "Runtime hello",
          correlationId: "thread-2",
          messageId: "msg-2",
          cost: null,
          timestamp: new Date().toISOString(),
        },
      ],
      totalCount: 2,
      page: 1,
      pageSize: 20,
    });
    mockGetAgentCostTimeseries.mockReset();
    mockGetAgentCostBreakdown.mockReset();
    mockGetUnitExecution.mockReset();
    mockGetUnitExecution.mockResolvedValue(otlpRuntimeExecution);
    mockGetAgentExecution.mockReset();
    mockGetAgent.mockReset();
  });

  it("renders the four filter chips", async () => {
    render(
      <Wrapper>
        <ActivityTab kind="Unit" id="eng-team" />
      </Wrapper>,
    );
    expect(
      await screen.findByTestId("tab-unit-activity-filters"),
    ).toBeInTheDocument();
    expect(
      screen.getByTestId("activity-filter-kind-select"),
    ).toBeInTheDocument();
    expect(
      screen.getByTestId("activity-filter-thread-select"),
    ).toBeInTheDocument();
    expect(
      screen.getByTestId("activity-filter-message-select"),
    ).toBeInTheDocument();
    expect(
      screen.getByTestId("activity-filter-from-input"),
    ).toBeInTheDocument();
    expect(
      screen.getByTestId("activity-filter-to-input"),
    ).toBeInTheDocument();
  });

  it("populates the thread dropdown from the loaded events", async () => {
    render(
      <Wrapper>
        <ActivityTab kind="Unit" id="eng-team" />
      </Wrapper>,
    );
    await waitFor(() => {
      expect(screen.getByText("Turn alpha")).toBeInTheDocument();
    });
    const select = screen.getByTestId(
      "activity-filter-thread-select",
    ) as HTMLSelectElement;
    const values = Array.from(select.options).map((o) => o.value);
    expect(values).toContain("thread-1");
    expect(values).toContain("thread-2");
  });

  it("time-range preset writes ISO from/to params to the URL", async () => {
    render(
      <Wrapper>
        <ActivityTab kind="Unit" id="eng-team" />
      </Wrapper>,
    );
    const preset = await screen.findByTestId("activity-filter-time-preset-5m");
    fireEvent.click(preset);
    expect(mockRouterReplace).toHaveBeenCalled();
    const url = mockRouterReplace.mock.calls[0][0] as string;
    expect(url).toContain("from=");
    expect(url).toContain("to=");
  });

  it("adding a kind via the dropdown writes the kind to the URL", async () => {
    render(
      <Wrapper>
        <ActivityTab kind="Unit" id="eng-team" />
      </Wrapper>,
    );
    await waitFor(() => {
      expect(screen.getByText("Turn alpha")).toBeInTheDocument();
    });
    const kindSelect = screen.getByTestId(
      "activity-filter-kind-select",
    ) as HTMLSelectElement;
    fireEvent.change(kindSelect, { target: { value: "LlmTurn" } });
    expect(mockRouterReplace).toHaveBeenCalled();
    const url = mockRouterReplace.mock.calls[0][0] as string;
    expect(url).toContain("kinds=LlmTurn");
  });
});

// #2564: the OTLP-only event kinds (`RuntimeLog` / `LlmTurn` /
// `RuntimeSpan`) arrive only via the OTLP ingest path and stay
// permanently empty for runtimes whose launcher emits no OTLP
// telemetry (`claude-code`). The Activity tab surfaces an inline hint
// so the operator does not add those dead chips and misread the feed.
describe("ActivityTab runtime-aware OTLP hint (#2564)", () => {
  beforeEach(() => {
    mockQueryActivity.mockReset();
    mockQueryActivity.mockResolvedValue({
      items: [],
      totalCount: 0,
      page: 1,
      pageSize: 20,
    });
    mockGetAgentCostTimeseries.mockReset();
    mockGetAgentCostTimeseries.mockResolvedValue(null);
    mockGetAgentCostBreakdown.mockReset();
    mockGetAgentCostBreakdown.mockResolvedValue(null);
    mockGetUnitExecution.mockReset();
    mockGetAgentExecution.mockReset();
    mockGetAgent.mockReset();
    mockGetAgent.mockResolvedValue(agentDetail(null));
  });

  it("shows the hint for a claude-code unit subject", async () => {
    mockGetUnitExecution.mockResolvedValue(noOtlpRuntimeExecution);
    render(
      <Wrapper>
        <ActivityTab kind="Unit" id="eng-team" />
      </Wrapper>,
    );
    const hint = await screen.findByTestId("activity-no-otlp-hint");
    expect(hint).toHaveTextContent(
      "This runtime does not emit OTLP telemetry",
    );
    expect(hint).toHaveTextContent("RuntimeLog / LlmTurn / RuntimeSpan");
  });

  it("hides the hint for a spring-voyage (OTLP-emitting) unit subject", async () => {
    mockGetUnitExecution.mockResolvedValue(otlpRuntimeExecution);
    render(
      <Wrapper>
        <ActivityTab kind="Unit" id="eng-team" />
      </Wrapper>,
    );
    await waitFor(() => {
      expect(
        screen.getByTestId("tab-unit-activity-filters"),
      ).toBeInTheDocument();
    });
    expect(
      screen.queryByTestId("activity-no-otlp-hint"),
    ).not.toBeInTheDocument();
  });

  it("shows the hint for a claude-code agent subject (own runtime)", async () => {
    mockGetAgentExecution.mockResolvedValue(noOtlpRuntimeExecution);
    render(
      <Wrapper>
        <ActivityTab kind="Agent" id="ada" />
      </Wrapper>,
    );
    expect(
      await screen.findByTestId("activity-no-otlp-hint"),
    ).toBeInTheDocument();
  });

  it("falls back to the owning unit's runtime when the agent inherits", async () => {
    // Agent declares no runtime — the effective value comes from the
    // owning unit's execution block.
    mockGetAgentExecution.mockResolvedValue({ runtime: null });
    mockGetAgent.mockResolvedValue(agentDetail("eng-team"));
    mockGetUnitExecution.mockResolvedValue(noOtlpRuntimeExecution);
    render(
      <Wrapper>
        <ActivityTab kind="Agent" id="ada" />
      </Wrapper>,
    );
    expect(
      await screen.findByTestId("activity-no-otlp-hint"),
    ).toBeInTheDocument();
  });

  it("hides the hint for a spring-voyage agent subject", async () => {
    mockGetAgentExecution.mockResolvedValue(otlpRuntimeExecution);
    render(
      <Wrapper>
        <ActivityTab kind="Agent" id="ada" />
      </Wrapper>,
    );
    await waitFor(() => {
      expect(
        screen.getByTestId("tab-agent-activity-filters"),
      ).toBeInTheDocument();
    });
    expect(
      screen.queryByTestId("activity-no-otlp-hint"),
    ).not.toBeInTheDocument();
  });

  it("stays silent when the runtime is not declared on the subject", async () => {
    // Neither the agent nor its owning unit declares a runtime — the
    // effective value is decided at dispatch, so the hint must not flash.
    mockGetAgentExecution.mockResolvedValue({ runtime: null });
    mockGetAgent.mockResolvedValue(agentDetail("eng-team"));
    mockGetUnitExecution.mockResolvedValue({ runtime: null });
    render(
      <Wrapper>
        <ActivityTab kind="Agent" id="ada" />
      </Wrapper>,
    );
    await waitFor(() => {
      expect(
        screen.getByTestId("tab-agent-activity-filters"),
      ).toBeInTheDocument();
    });
    expect(
      screen.queryByTestId("activity-no-otlp-hint"),
    ).not.toBeInTheDocument();
  });
});
