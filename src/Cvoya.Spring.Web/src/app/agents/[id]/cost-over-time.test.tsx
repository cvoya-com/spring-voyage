import { render, screen, waitFor } from "@testing-library/react";
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { beforeEach, describe, expect, it, vi } from "vitest";
import type { ReactNode } from "react";

import type { CostSummaryResponse } from "@/lib/api/types";

const getAgentCost =
  vi.fn<
    (
      id: string,
      range?: { from?: string; to?: string },
    ) => Promise<CostSummaryResponse>
  >();

vi.mock("@/lib/api/client", () => ({
  api: {
    getAgentCost: (id: string, range?: { from?: string; to?: string }) =>
      getAgentCost(id, range),
  },
}));

vi.mock("next/link", () => ({
  default: ({
    href,
    children,
    ...rest
  }: {
    href: string;
    children: React.ReactNode;
  } & Record<string, unknown>) => (
    <a href={href} {...rest}>
      {children}
    </a>
  ),
}));

import { CostOverTimeCard } from "./cost-over-time";

function renderCard(id: string) {
  const client = new QueryClient({
    defaultOptions: {
      queries: { retry: false, gcTime: 0, staleTime: 0 },
    },
  });
  const Wrapper = ({ children }: { children: ReactNode }) => (
    <QueryClientProvider client={client}>{children}</QueryClientProvider>
  );
  return render(<CostOverTimeCard agentId={id} />, { wrapper: Wrapper });
}

function makeSummary(
  overrides: Partial<CostSummaryResponse> = {},
): CostSummaryResponse {
  return {
    totalCost: 1,
    totalInputTokens: 10,
    totalOutputTokens: 5,
    recordCount: 1,
    workCost: 1,
    initiativeCost: 0,
    from: "2026-04-01T00:00:00Z",
    to: "2026-04-17T00:00:00Z",
    ...overrides,
  } as CostSummaryResponse;
}

describe("CostOverTimeCard", () => {
  beforeEach(() => {
    getAgentCost.mockReset();
  });

  it("renders 24h / 7d / 30d window bars and the Analytics cross-link", async () => {
    getAgentCost.mockResolvedValue(makeSummary({ totalCost: 2.5 }));

    renderCard("engineering/ada");

    await waitFor(() => {
      // Three windowed calls: 24h, 7d, 30d.
      expect(getAgentCost).toHaveBeenCalledTimes(3);
    });

    expect(await screen.findByTestId("agent-cost-24h")).toBeInTheDocument();
    expect(screen.getByTestId("agent-cost-7d")).toBeInTheDocument();
    expect(screen.getByTestId("agent-cost-30d")).toBeInTheDocument();
    expect(
      screen.getByTestId("agent-cost-analytics-link"),
    ).toHaveAttribute(
      "href",
      "/analytics/costs?scope=agent&name=engineering%2Fada",
    );
  });

  it("calls getAgentCost with distinct (from, to) windows", async () => {
    getAgentCost.mockResolvedValue(makeSummary({ totalCost: 0 }));

    renderCard("engineering/ada");

    await waitFor(() => {
      expect(getAgentCost).toHaveBeenCalledTimes(3);
    });

    // All three calls receive a range argument — different `from` values.
    const rangeValues = getAgentCost.mock.calls
      .map((call) => call[1]?.from ?? "")
      .filter(Boolean);
    expect(new Set(rangeValues).size).toBe(3);
  });
});
