import { render, screen, fireEvent, waitFor } from "@testing-library/react";
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { describe, expect, it, vi, beforeEach } from "vitest";
import type { ReactNode } from "react";

import ActivityPage from "./page";

const mockQueryActivity = vi.fn();

vi.mock("@/lib/api/client", () => ({
  api: {
    queryActivity: (...args: unknown[]) => mockQueryActivity(...args),
  },
}));

vi.mock("next/navigation", () => ({
  usePathname: () => "/activity",
  useRouter: () => ({ push: vi.fn() }),
}));

// Wrap the page in a fresh QueryClient per test so TanStack Query
// doesn't replay cached responses between cases (#438 migration).
function renderPage() {
  const client = new QueryClient({
    defaultOptions: {
      queries: { retry: false, gcTime: 0, staleTime: 0 },
    },
  });
  const Wrapper = ({ children }: { children: ReactNode }) => (
    <QueryClientProvider client={client}>{children}</QueryClientProvider>
  );
  return render(<ActivityPage />, { wrapper: Wrapper });
}

const mockResult = {
  items: [
    {
      id: "evt-1",
      source: "unit://eng-team",
      eventType: "StateChanged",
      severity: "Info",
      summary: "Unit started successfully",
      correlationId: "corr-123",
      cost: 0.0042,
      timestamp: new Date().toISOString(),
    },
    {
      id: "evt-2",
      source: "agent://ada",
      eventType: "MessageArrived",
      severity: "Warning",
      summary: "Slow response from model",
      correlationId: null,
      cost: null,
      timestamp: new Date().toISOString(),
    },
  ],
  totalCount: 2,
  page: 1,
  pageSize: 20,
};

describe("ActivityPage", () => {
  beforeEach(() => {
    mockQueryActivity.mockReset();
    mockQueryActivity.mockResolvedValue(mockResult);
  });

  it("renders activity events from the API", async () => {
    renderPage();
    await waitFor(() => {
      expect(screen.getByText("Unit started successfully")).toBeInTheDocument();
      expect(
        screen.getByText("Slow response from model"),
      ).toBeInTheDocument();
    });
  });

  it("passes filter parameters to the API", async () => {
    renderPage();
    await waitFor(() => {
      expect(mockQueryActivity).toHaveBeenCalled();
    });

    const sourceInput = screen.getByPlaceholderText("e.g. unit:my-unit");
    fireEvent.change(sourceInput, { target: { value: "unit:eng-team" } });

    await waitFor(() => {
      const lastCall =
        mockQueryActivity.mock.calls[mockQueryActivity.mock.calls.length - 1];
      expect(lastCall[0]).toMatchObject({ source: "unit:eng-team" });
    });
  });

  it("expands event details on click", async () => {
    renderPage();
    await waitFor(() => {
      expect(screen.getByText("Unit started successfully")).toBeInTheDocument();
    });

    // Click on the first event row button
    const buttons = screen.getAllByRole("button");
    const rowButton = buttons.find((b) =>
      b.textContent?.includes("Unit started successfully"),
    );
    expect(rowButton).toBeDefined();
    fireEvent.click(rowButton!);

    expect(screen.getByText("corr-123")).toBeInTheDocument();
    expect(screen.getByText("$0.0042")).toBeInTheDocument();
  });

  it("shows total count", async () => {
    renderPage();
    await waitFor(() => {
      expect(screen.getByText("2 total")).toBeInTheDocument();
    });
  });

  it("shows empty state when no events", async () => {
    mockQueryActivity.mockResolvedValue({
      items: [],
      totalCount: 0,
      page: 1,
      pageSize: 20,
    });
    renderPage();
    await waitFor(() => {
      expect(
        screen.getByText("No activity events found."),
      ).toBeInTheDocument();
    });
  });
});
