import { render, screen, waitFor, within } from "@testing-library/react";
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { beforeEach, describe, expect, it, vi } from "vitest";
import type { ReactNode } from "react";

import type { CostSummaryResponse } from "@/lib/api/types";

const getTenantCost =
  vi.fn<
    (range?: {
      from?: string;
      to?: string;
    }) => Promise<CostSummaryResponse>
  >();

vi.mock("@/lib/api/client", () => ({
  api: {
    getTenantCost: (range?: { from?: string; to?: string }) =>
      getTenantCost(range),
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

import {
  CostSummaryCard,
  SpendStatTile,
  resolveCostWindows,
} from "./cost-summary-card";

function renderCard(props: { thirtyDaySeries?: number[] } = {}) {
  const client = new QueryClient({
    defaultOptions: {
      queries: { retry: false, gcTime: 0, staleTime: 0 },
    },
  });
  const Wrapper = ({ children }: { children: ReactNode }) => (
    <QueryClientProvider client={client}>{children}</QueryClientProvider>
  );
  return render(<CostSummaryCard {...props} />, { wrapper: Wrapper });
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

describe("resolveCostWindows", () => {
  it("buckets today at UTC midnight → now", () => {
    const now = new Date("2026-04-17T14:30:00Z");
    const w = resolveCostWindows(now);
    expect(w.today.to).toBe(now.toISOString());
    expect(w.today.from).toBe("2026-04-17T00:00:00.000Z");
  });

  it("rolls 7d and 30d windows ending at now", () => {
    const now = new Date("2026-04-17T14:30:00Z");
    const w = resolveCostWindows(now);
    expect(w.sevenDay.to).toBe(now.toISOString());
    expect(w.sevenDay.from).toBe("2026-04-10T14:30:00.000Z");
    expect(w.thirtyDay.from).toBe("2026-03-18T14:30:00.000Z");
  });
});

describe("SpendStatTile", () => {
  it("renders label + value in mono tabular-nums", () => {
    render(
      <SpendStatTile
        label="Today"
        value={12.5}
        pending={false}
        testId="stat-today"
      />,
    );
    expect(screen.getByText("Today")).toBeInTheDocument();
    const value = screen.getByTestId("stat-today-value");
    expect(value).toHaveTextContent("$12.50");
    expect(value.className).toMatch(/font-mono/);
    expect(value.className).toMatch(/tabular-nums/);
  });

  it("renders a Skeleton when pending", () => {
    const { container } = render(
      <SpendStatTile
        label="Today"
        value={null}
        pending
        testId="stat-today"
      />,
    );
    // Skeleton primitive has `animate-pulse` — reach for it via class
    // since the primitive doesn't carry a stable testid.
    expect(container.querySelector(".animate-pulse")).not.toBeNull();
  });

  it("renders a sparkline when a series is provided", () => {
    render(
      <SpendStatTile
        label="Last 30d"
        value={42}
        pending={false}
        series={[1, 2, 4, 3, 6]}
        testId="stat-30d"
      />,
    );
    expect(screen.getByTestId("stat-30d-sparkline")).toBeInTheDocument();
  });

  it("omits the sparkline when no series is provided", () => {
    render(
      <SpendStatTile
        label="Today"
        value={1}
        pending={false}
        testId="stat-today"
      />,
    );
    expect(screen.queryByTestId("stat-today-sparkline")).toBeNull();
  });
});

describe("CostSummaryCard", () => {
  beforeEach(() => {
    getTenantCost.mockReset();
  });

  it("renders the three window tiles with formatted totals", async () => {
    // Reply with totals indexed by call order (3 queries).
    let call = 0;
    getTenantCost.mockImplementation(() => {
      call += 1;
      return Promise.resolve(makeSummary({ totalCost: call * 10 }));
    });

    renderCard();

    await waitFor(() => {
      expect(getTenantCost).toHaveBeenCalledTimes(3);
    });

    const card = await screen.findByTestId("cost-summary-card");
    expect(card).toBeInTheDocument();
    // All three tiles are present.
    expect(within(card).getByTestId("cost-summary-today")).toBeInTheDocument();
    expect(within(card).getByTestId("cost-summary-7d")).toBeInTheDocument();
    expect(within(card).getByTestId("cost-summary-30d")).toBeInTheDocument();
    // Details link points at /analytics/costs.
    expect(
      within(card).getByTestId("cost-summary-link"),
    ).toHaveAttribute("href", "/analytics/costs");
  });

  it("renders an em-dash placeholder when the API errors", async () => {
    getTenantCost.mockRejectedValue(new Error("boom"));

    renderCard();

    await waitFor(() => {
      expect(getTenantCost).toHaveBeenCalled();
    });

    const today = await screen.findByTestId("cost-summary-today");
    await waitFor(() => {
      expect(today.textContent).toContain("—");
    });
  });

  it("renders a sparkline on the 30d tile when thirtyDaySeries is provided", async () => {
    getTenantCost.mockResolvedValue(makeSummary({ totalCost: 1 }));

    renderCard({ thirtyDaySeries: [1, 2, 3, 5, 4, 7] });

    const thirty = await screen.findByTestId("cost-summary-30d");
    await waitFor(() => {
      expect(
        within(thirty).getByTestId("cost-summary-30d-sparkline"),
      ).toBeInTheDocument();
    });
    // Today + 7d get no sparkline series by default.
    expect(screen.queryByTestId("cost-summary-today-sparkline")).toBeNull();
    expect(screen.queryByTestId("cost-summary-7d-sparkline")).toBeNull();
  });

  it("renders tile values in mono tabular-nums (v2 design system)", async () => {
    getTenantCost.mockResolvedValue(makeSummary({ totalCost: 3.5 }));

    renderCard();

    const value = await screen.findByTestId("cost-summary-today-value");
    expect(value.className).toMatch(/font-mono/);
    expect(value.className).toMatch(/tabular-nums/);
  });
});
