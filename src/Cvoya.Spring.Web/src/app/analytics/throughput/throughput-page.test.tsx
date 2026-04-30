import { render, screen, waitFor } from "@testing-library/react";
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { beforeEach, describe, expect, it, vi } from "vitest";
import type { ReactNode } from "react";

import type { ThroughputRollupResponse } from "@/lib/api/types";

const getAnalyticsThroughput =
  vi.fn<
    (params?: {
      source?: string;
      from?: string;
      to?: string;
    }) => Promise<ThroughputRollupResponse>
  >();

vi.mock("@/lib/api/client", () => ({
  api: {
    getAnalyticsThroughput: (...args: unknown[]) =>
      getAnalyticsThroughput(
        ...(args as [
          { source?: string; from?: string; to?: string } | undefined,
        ]),
      ),
  },
}));

vi.mock("next/navigation", () => ({
  useRouter: () => ({ replace: vi.fn(), push: vi.fn() }),
  // #1053: `useAnalyticsFilters` now reads `usePathname()` so it can
  // pass a `/path?query` URL to `router.replace`.
  usePathname: () => "/analytics/throughput",
  useSearchParams: () => new URLSearchParams(""),
}));

vi.mock("next/link", () => ({
  default: ({
    href,
    children,
    ...rest
  }: {
    href: string;
    children: ReactNode;
  } & Record<string, unknown>) => (
    <a href={href} {...rest}>
      {children}
    </a>
  ),
}));

import AnalyticsThroughputPage from "./page";

function renderPage() {
  const client = new QueryClient({
    defaultOptions: {
      queries: { retry: false, gcTime: 0, staleTime: 0 },
      mutations: { retry: false },
    },
  });
  const Wrapper = ({ children }: { children: ReactNode }) => (
    <QueryClientProvider client={client}>{children}</QueryClientProvider>
  );
  return render(<AnalyticsThroughputPage />, { wrapper: Wrapper });
}

describe("AnalyticsThroughputPage", () => {
  beforeEach(() => {
    getAnalyticsThroughput.mockReset();
  });

  it("renders the per-source counters grid when data arrives", async () => {
    getAnalyticsThroughput.mockResolvedValue({
      entries: [
        {
          source: "agent://ada",
          messagesReceived: 10,
          messagesSent: 8,
          turns: 4,
          toolCalls: 3,
        },
        {
          source: "unit://eng-team",
          messagesReceived: 30,
          messagesSent: 20,
          turns: 10,
          toolCalls: 5,
        },
      ],
      from: "2026-04-01T00:00:00Z",
      to: "2026-04-16T00:00:00Z",
    });

    renderPage();

    // The virtualised grid container should appear once data loads.
    await waitFor(() => {
      expect(
        screen.getByRole("grid", { name: "Per-source throughput counters" }),
      ).toBeInTheDocument();
    });
    // KPI strip totals: received = 10+30=40, sent = 8+20=28.
    expect(screen.getByText("40")).toBeInTheDocument();
    expect(screen.getByText("28")).toBeInTheDocument();
  });

  it("renders the virtualised table with role=grid for per-source counters", async () => {
    getAnalyticsThroughput.mockResolvedValue({
      entries: [
        {
          source: "agent://ada",
          messagesReceived: 10,
          messagesSent: 8,
          turns: 4,
          toolCalls: 3,
        },
      ],
      from: "2026-04-01T00:00:00Z",
      to: "2026-04-16T00:00:00Z",
    });

    renderPage();

    await waitFor(() => {
      expect(
        screen.getByRole("grid", { name: "Per-source throughput counters" }),
      ).toBeInTheDocument();
    });
  });

  it("renders the stacked bar chart when data is present", async () => {
    getAnalyticsThroughput.mockResolvedValue({
      entries: [
        {
          source: "agent://ada",
          messagesReceived: 5,
          messagesSent: 3,
          turns: 2,
          toolCalls: 1,
        },
      ],
      from: "2026-04-01T00:00:00Z",
      to: "2026-04-16T00:00:00Z",
    });

    renderPage();

    await waitFor(() => {
      expect(
        screen.getByRole("img", { name: /throughput per source/i }),
      ).toBeInTheDocument();
    });
  });

  it("renders the empty state when there are no entries", async () => {
    getAnalyticsThroughput.mockResolvedValue({
      entries: [],
      from: "2026-04-01T00:00:00Z",
      to: "2026-04-16T00:00:00Z",
    });

    renderPage();

    await waitFor(() => {
      expect(
        screen.getByText(/No throughput in this window\./i),
      ).toBeInTheDocument();
    });
  });

  it("exposes a CLI-equivalent hint that mirrors the selected window", async () => {
    getAnalyticsThroughput.mockResolvedValue({
      entries: [],
      from: "2026-04-01T00:00:00Z",
      to: "2026-04-16T00:00:00Z",
    });

    renderPage();

    await waitFor(() => {
      expect(
        screen.getByText(/spring analytics throughput --window 30d/),
      ).toBeInTheDocument();
    });
  });
});
