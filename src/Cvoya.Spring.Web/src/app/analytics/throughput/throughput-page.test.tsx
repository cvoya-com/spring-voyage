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

  it("renders per-source counters sorted by total", async () => {
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

    await waitFor(() => {
      expect(screen.getByText("unit://eng-team")).toBeInTheDocument();
    });
    expect(screen.getByText("agent://ada")).toBeInTheDocument();
    // Total = 30+20+10+5 = 65 for eng-team (highest).
    expect(screen.getByText("65")).toBeInTheDocument();
    // Sort: eng-team's row renders before ada's.
    const rowTexts = screen.getAllByRole("link").map((el) => el.textContent);
    const engIdx = rowTexts.findIndex((t) => t === "unit://eng-team");
    const adaIdx = rowTexts.findIndex((t) => t === "agent://ada");
    expect(engIdx).toBeLessThan(adaIdx);
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
