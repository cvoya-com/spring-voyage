import { render, screen, waitFor } from "@testing-library/react";
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { beforeEach, describe, expect, it, vi } from "vitest";
import type { ReactNode } from "react";

import type { WaitTimeRollupResponse } from "@/lib/api/types";

const getAnalyticsWaits =
  vi.fn<
    (params?: {
      source?: string;
      from?: string;
      to?: string;
    }) => Promise<WaitTimeRollupResponse>
  >();

vi.mock("@/lib/api/client", () => ({
  api: {
    getAnalyticsWaits: (...args: unknown[]) =>
      getAnalyticsWaits(
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
  usePathname: () => "/analytics/waits",
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

import AnalyticsWaitsPage from "./page";

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
  return render(<AnalyticsWaitsPage />, { wrapper: Wrapper });
}

describe("AnalyticsWaitsPage", () => {
  beforeEach(() => {
    getAnalyticsWaits.mockReset();
  });

  it("renders the per-source durations grid when data arrives", async () => {
    getAnalyticsWaits.mockResolvedValue({
      entries: [
        {
          source: "agent://ada",
          idleSeconds: 120,
          busySeconds: 60,
          waitingForHumanSeconds: 30,
          stateTransitions: 12,
        },
      ],
      from: "2026-04-01T00:00:00Z",
      to: "2026-04-16T00:00:00Z",
    });

    renderPage();

    // The virtualised grid container should appear once data loads.
    await waitFor(() => {
      expect(
        screen.getByRole("grid", { name: "Per-source wait-time durations" }),
      ).toBeInTheDocument();
    });
    // KPI strip shows the aggregated idle time: 120s → "2m 0s".
    expect(screen.getByText("2m 0s")).toBeInTheDocument();
  });

  it("renders the virtualised table with role=grid", async () => {
    getAnalyticsWaits.mockResolvedValue({
      entries: [
        {
          source: "agent://ada",
          idleSeconds: 60,
          busySeconds: 30,
          waitingForHumanSeconds: 0,
          stateTransitions: 5,
        },
      ],
      from: "2026-04-01T00:00:00Z",
      to: "2026-04-16T00:00:00Z",
    });

    renderPage();

    await waitFor(() => {
      expect(
        screen.getByRole("grid", { name: "Per-source wait-time durations" }),
      ).toBeInTheDocument();
    });
  });

  it("renders the stacked bar chart when data is present", async () => {
    getAnalyticsWaits.mockResolvedValue({
      entries: [
        {
          source: "agent://ada",
          idleSeconds: 120,
          busySeconds: 60,
          waitingForHumanSeconds: 30,
          stateTransitions: 12,
        },
      ],
      from: "2026-04-01T00:00:00Z",
      to: "2026-04-16T00:00:00Z",
    });

    renderPage();

    await waitFor(() => {
      expect(
        screen.getByRole("img", { name: /wait times per source/i }),
      ).toBeInTheDocument();
    });
  });

  it("renders the empty state when no transitions occurred", async () => {
    getAnalyticsWaits.mockResolvedValue({
      entries: [],
      from: "2026-04-01T00:00:00Z",
      to: "2026-04-16T00:00:00Z",
    });

    renderPage();

    await waitFor(() => {
      expect(
        screen.getByText(/No state transitions in this window\./i),
      ).toBeInTheDocument();
    });
  });

  it("exposes a CLI-equivalent hint that mirrors the selected window", async () => {
    getAnalyticsWaits.mockResolvedValue({
      entries: [],
      from: "2026-04-01T00:00:00Z",
      to: "2026-04-16T00:00:00Z",
    });

    renderPage();

    await waitFor(() => {
      expect(
        screen.getByText(/spring analytics waits --window 30d/),
      ).toBeInTheDocument();
    });
  });
});
