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

  it("renders per-source durations and transition counts", async () => {
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
      expect(screen.getByText("agent://ada")).toBeInTheDocument();
    });
    // formatDuration: 120s → "2m 0s", 60s → "1m 0s", 30s → "30s".
    // The row layout renders each value twice (once in the mobile 2×2
    // grid, once in the desktop table row — the responsive pass in
    // #445 stacks rows on narrow viewports and the desktop cells are
    // just hidden, not unmounted), so use `getAllByText` and assert
    // at least one match.
    expect(screen.getAllByText("2m 0s").length).toBeGreaterThan(0);
    expect(screen.getAllByText("1m 0s").length).toBeGreaterThan(0);
    expect(screen.getAllByText("30s").length).toBeGreaterThan(0);
    // StateTransitions counter is rendered.
    expect(screen.getAllByText("12").length).toBeGreaterThan(0);
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
