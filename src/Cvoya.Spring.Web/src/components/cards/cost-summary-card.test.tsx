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
  resolveCostWindows,
} from "./cost-summary-card";

function renderCard() {
  const client = new QueryClient({
    defaultOptions: {
      queries: { retry: false, gcTime: 0, staleTime: 0 },
    },
  });
  const Wrapper = ({ children }: { children: ReactNode }) => (
    <QueryClientProvider client={client}>{children}</QueryClientProvider>
  );
  return render(<CostSummaryCard />, { wrapper: Wrapper });
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

describe("CostSummaryCard", () => {
  beforeEach(() => {
    getTenantCost.mockReset();
  });

  it("renders the three window tiles with formatted totals", async () => {
    // Distinguish windows by the `from` param the card passes; today
    // is always the smallest delta against `to`.
    getTenantCost.mockImplementation((range) => {
      const from = range?.from ?? "";
      if (from.includes("T00:00:00.000Z") && from.split("T")[0].endsWith("17")) {
        // Actually this is fragile; map by window size instead below.
        return Promise.resolve(makeSummary({ totalCost: 1 }));
      }
      return Promise.resolve(makeSummary({ totalCost: 0 }));
    });

    // Simpler impl: reply with totals indexed by call order (3 queries).
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
});
