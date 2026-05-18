// Route-level smoke tests for the Human detail page (#2266 / #2267).
//
// Mirrors the Explorer's route-test pattern from `units-page.test.tsx`:
// the page is fully client-side (no RSC navigation), so we stub
// `next/navigation`, the tab-mounting registry runs at module top-level,
// and the human-overview hooks are stubbed with deterministic data so
// the test asserts page composition rather than network behaviour.

import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { fireEvent, render, screen, waitFor } from "@testing-library/react";
import type { ReactNode } from "react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

const notFoundMock = vi.fn(() => {
  throw new Error("NEXT_NOT_FOUND");
});
let currentSearchParams = new URLSearchParams();
const historyReplaceStateMock = vi.fn();
let currentParams: { id: string } = { id: "" };

vi.mock("next/navigation", () => ({
  useParams: () => currentParams,
  usePathname: () => `/humans/${currentParams.id}`,
  useSearchParams: () => currentSearchParams,
  notFound: () => notFoundMock(),
}));

const useHumanMock = vi.fn();
const useCurrentUserMock = vi.fn();
const useUnitMock = vi.fn(() => ({ data: null }));
const useAgentMock = vi.fn(() => ({ data: null }));

vi.mock("@/lib/api/queries", () => ({
  useHuman: (...args: unknown[]) => useHumanMock(...args),
  useCurrentUser: () => useCurrentUserMock(),
  useUnit: () => useUnitMock(),
  useAgent: () => useAgentMock(),
  // Stubs for other hooks the registered tabs / Detail Pane chrome
  // might pull in transitively. We never see these in the rendered
  // output for this smoke layer, but ensuring they exist keeps the
  // tab registry import from blowing up at module-load time.
  useUnitOwnExpertise: () => ({ data: [], isPending: false }),
  useUnitAggregatedExpertise: () => ({
    data: { entries: [] },
    isPending: false,
  }),
  useUnitCostTimeseries: () => ({ data: null, isLoading: false }),
  useUnitExecution: () => ({ data: null, isLoading: false }),
  useUnitIssues: () => ({ data: null, isPending: false, isError: false }),
  useAgentIssues: () => ({ data: null, isPending: false, isError: false }),
  useAgentCost: () => ({ data: null, isPending: false }),
  useIssueCounts: () => ({
    data: { counts: [] },
    isPending: false,
    isError: false,
  }),
  useModelProviders: () => ({ data: [], isLoading: false }),
}));

import HumanDetailPage from "./page";

function wrap(node: ReactNode) {
  const client = new QueryClient({
    defaultOptions: { queries: { retry: false, gcTime: 0 } },
  });
  return <QueryClientProvider client={client}>{node}</QueryClientProvider>;
}

const SAMPLE_GUID = "11111111-1111-1111-1111-111111111111";

beforeEach(() => {
  currentSearchParams = new URLSearchParams();
  currentParams = { id: SAMPLE_GUID };
  notFoundMock.mockClear();
  historyReplaceStateMock.mockClear();
  useHumanMock.mockReset();
  useCurrentUserMock.mockReset();
  vi.spyOn(window.history, "replaceState").mockImplementation(
    (state, title, url) => {
      historyReplaceStateMock(state, title, url);
    },
  );
});

afterEach(() => {
  vi.restoreAllMocks();
});

describe("HumanDetailPage — /humans/[id] (#2266 / #2267)", () => {
  it("renders the loading state while the human entity is fetching", () => {
    useHumanMock.mockReturnValue({
      data: undefined,
      isLoading: true,
      isError: false,
    });
    useCurrentUserMock.mockReturnValue({ data: null });
    render(wrap(<HumanDetailPage />));
    expect(screen.getByTestId("human-detail-loading")).toBeInTheDocument();
  });

  it("renders the detail pane chrome once the human lands", async () => {
    useHumanMock.mockReturnValue({
      data: {
        id: SAMPLE_GUID,
        username: "savas",
        displayName: "Savas Parastatidis",
        email: "savas@example.com",
        platformRole: "Owner",
        createdAt: "2026-01-15T10:00:00Z",
      },
      isLoading: false,
      isError: false,
    });
    useCurrentUserMock.mockReturnValue({
      data: { id: SAMPLE_GUID, displayName: "Savas", userId: "savas" },
    });

    render(wrap(<HumanDetailPage />));
    expect(
      await screen.findByTestId("human-detail-route"),
    ).toBeInTheDocument();
    // The Detail Pane renders Overview by default — the body shell is
    // the kind-keyed test-id from `overview-tab.tsx`.
    expect(
      await screen.findByTestId("tab-human-overview"),
    ).toBeInTheDocument();
  });

  it("renders the You hint when the loaded human is the current user", async () => {
    useHumanMock.mockReturnValue({
      data: {
        id: SAMPLE_GUID,
        username: "savas",
        displayName: "Savas",
        email: "savas@example.com",
        platformRole: "Owner",
        createdAt: "2026-01-15T10:00:00Z",
      },
      isLoading: false,
      isError: false,
    });
    useCurrentUserMock.mockReturnValue({
      data: { id: SAMPLE_GUID, displayName: "Savas", userId: "savas" },
    });

    render(wrap(<HumanDetailPage />));
    expect(
      await screen.findByTestId("tab-human-overview-you-hint"),
    ).toHaveTextContent(/you/i);
  });

  it("does NOT render the You hint when the loaded human is someone else", async () => {
    useHumanMock.mockReturnValue({
      data: {
        id: SAMPLE_GUID,
        username: "teammate",
        displayName: "Teammate",
        email: null,
        platformRole: "Operator",
        createdAt: "2026-02-01T08:30:00Z",
      },
      isLoading: false,
      isError: false,
    });
    useCurrentUserMock.mockReturnValue({
      data: {
        id: "99999999-9999-9999-9999-999999999999",
        displayName: "Someone Else",
        userId: "someoneelse",
      },
    });

    render(wrap(<HumanDetailPage />));
    expect(
      await screen.findByTestId("tab-human-overview"),
    ).toBeInTheDocument();
    expect(
      screen.queryByTestId("tab-human-overview-you-hint"),
    ).toBeNull();
  });

  it("renders the missing-human empty state when the entity comes back null", async () => {
    useHumanMock.mockReturnValue({
      data: null,
      isLoading: false,
      isError: false,
    });
    useCurrentUserMock.mockReturnValue({ data: null });

    // The page collapses null + non-loading into a notFound() — assert
    // the call rather than the render output, since the boundary is
    // the framework's, not ours.
    expect(() => render(wrap(<HumanDetailPage />))).toThrow(
      "NEXT_NOT_FOUND",
    );
    expect(notFoundMock).toHaveBeenCalled();
  });

  it("treats a non-UUID [id] segment as a 404 — never hits the API", () => {
    currentParams = { id: "not-a-guid" };
    useHumanMock.mockReturnValue({
      data: undefined,
      isLoading: false,
      isError: false,
    });
    useCurrentUserMock.mockReturnValue({ data: null });
    expect(() => render(wrap(<HumanDetailPage />))).toThrow(
      "NEXT_NOT_FOUND",
    );
  });

  it("writes ?tab=<Tab> to the URL when a tab is clicked", async () => {
    useHumanMock.mockReturnValue({
      data: {
        id: SAMPLE_GUID,
        username: "savas",
        displayName: "Savas",
        email: "savas@example.com",
        platformRole: "Owner",
        createdAt: "2026-01-15T10:00:00Z",
      },
      isLoading: false,
      isError: false,
    });
    useCurrentUserMock.mockReturnValue({ data: null });

    render(wrap(<HumanDetailPage />));
    await screen.findByTestId("human-detail-route");

    // Messages is in the visible strip per HUMAN_TABS.
    fireEvent.click(screen.getByTestId("detail-tab-messages"));
    await waitFor(() =>
      expect(historyReplaceStateMock).toHaveBeenCalled(),
    );
    const last =
      historyReplaceStateMock.mock.calls.at(-1)?.[2]?.toString() ?? "";
    expect(last).toMatch(/tab=Messages/);
  });
});
