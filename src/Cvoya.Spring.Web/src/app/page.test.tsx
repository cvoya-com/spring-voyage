import { render, screen, waitFor } from "@testing-library/react";
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { beforeEach, describe, expect, it, vi } from "vitest";
import type { ReactNode } from "react";

import type { DashboardSummary } from "@/lib/api/types";

const getDashboardSummary =
  vi.fn<() => Promise<DashboardSummary>>();

vi.mock("@/lib/api/client", () => ({
  api: {
    getDashboardSummary: () => getDashboardSummary(),
  },
}));

// The dashboard now subscribes to the activity stream for live
// refreshes. Stub the hook so tests don't open a real EventSource.
vi.mock("@/lib/stream/use-activity-stream", () => ({
  useActivityStream: () => ({ events: [], connected: false }),
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

import DashboardPage from "./page";

function renderDashboard() {
  const client = new QueryClient({
    defaultOptions: {
      queries: { retry: false, gcTime: 0, staleTime: 0 },
    },
  });
  const Wrapper = ({ children }: { children: ReactNode }) => (
    <QueryClientProvider client={client}>{children}</QueryClientProvider>
  );
  return render(<DashboardPage />, { wrapper: Wrapper });
}

function makeSummary(
  overrides: Partial<DashboardSummary> = {},
): DashboardSummary {
  return {
    unitCount: 2,
    unitsByStatus: { Draft: 1, Running: 1 },
    agentCount: 3,
    recentActivity: [],
    totalCost: 42.5,
    units: [
      {
        name: "unit-alpha",
        displayName: "Unit Alpha",
        registeredAt: "2026-01-01T00:00:00Z",
        status: "Running",
      },
      {
        name: "unit-beta",
        displayName: "Unit Beta",
        registeredAt: "2026-01-02T00:00:00Z",
        status: "Draft",
      },
    ],
    agents: [
      {
        name: "unit-alpha/agent-1",
        displayName: "Agent One",
        role: "backend",
        registeredAt: "2026-01-01T00:00:00Z",
      },
      {
        name: "agent-2",
        displayName: "Agent Two",
        role: null,
        registeredAt: "2026-01-01T00:00:00Z",
      },
      {
        name: "unit-alpha/agent-3",
        displayName: "Agent Three",
        role: "frontend",
        registeredAt: "2026-01-01T00:00:00Z",
      },
    ],
    ...overrides,
  };
}

describe("DashboardPage", () => {
  beforeEach(() => {
    getDashboardSummary.mockReset();
  });

  it("renders all three sections with cards when data is populated", async () => {
    getDashboardSummary.mockResolvedValue(
      makeSummary({
        recentActivity: [
          {
            id: "evt-1",
            source: "agent://unit-alpha/agent-1",
            eventType: "MessageReceived",
            severity: "Info",
            summary: "Agent received a message",
            timestamp: "2026-04-13T10:00:00Z",
          },
        ],
      }),
    );

    renderDashboard();

    // All three sections render
    await waitFor(() => {
      expect(screen.getByText("Unit Alpha")).toBeInTheDocument();
    });
    expect(screen.getByText("Agent One")).toBeInTheDocument();
    // Activity text appears in both the activity feed and the agent card preview
    const activityTexts = screen.getAllByText("Agent received a message");
    expect(activityTexts.length).toBeGreaterThanOrEqual(1);
  });

  it("renders stats header with correct breakdown", async () => {
    getDashboardSummary.mockResolvedValue(
      makeSummary({
        unitsByStatus: { Running: 1, Draft: 1, Error: 1 },
        unitCount: 3,
      }),
    );

    renderDashboard();

    await waitFor(() => {
      expect(screen.getByTestId("stats-header")).toBeInTheDocument();
    });

    // Unit count (also matches agentCount which defaults to 3)
    const threes = screen.getAllByText("3");
    expect(threes.length).toBeGreaterThanOrEqual(1);

    // Status breakdown badges
    expect(screen.getByTestId("units-running-badge")).toHaveTextContent(
      "1 running",
    );
    expect(screen.getByTestId("units-stopped-badge")).toHaveTextContent(
      "1 stopped",
    );
    expect(screen.getByTestId("units-error-badge")).toHaveTextContent(
      "1 error",
    );

    // Agent count label appears in both the stats header and section heading
    const agentLabels = screen.getAllByText("Agents");
    expect(agentLabels.length).toBeGreaterThanOrEqual(1);

    // Total cost
    expect(screen.getByText("$42.50")).toBeInTheDocument();

    // Health should be degraded (error units)
    expect(screen.getByTestId("health-label")).toHaveTextContent("Degraded");
  });

  it("shows healthy system health when all units are running", async () => {
    getDashboardSummary.mockResolvedValue(
      makeSummary({
        unitsByStatus: { Running: 2 },
      }),
    );

    renderDashboard();

    await waitFor(() => {
      expect(screen.getByTestId("health-label")).toHaveTextContent("Healthy");
    });
  });

  it("unit card click navigates to unit detail page", async () => {
    getDashboardSummary.mockResolvedValue(makeSummary());

    renderDashboard();

    await waitFor(() => {
      expect(screen.getByTestId("unit-card-unit-alpha")).toBeInTheDocument();
    });
    const link = screen.getByTestId("unit-card-unit-alpha");
    expect(link).toHaveAttribute("href", "/units/unit-alpha");
  });

  it("unit cards display status badge and status dot", async () => {
    getDashboardSummary.mockResolvedValue(makeSummary());

    renderDashboard();

    await waitFor(() => {
      expect(screen.getByText("Unit Alpha")).toBeInTheDocument();
    });
    expect(screen.getByText("Running")).toBeInTheDocument();
    expect(screen.getByText("Draft")).toBeInTheDocument();

    // Status dot elements
    expect(
      screen.getByTestId("unit-status-dot-unit-alpha"),
    ).toBeInTheDocument();
  });

  it("agent card shows parent-unit badge when agent is nested", async () => {
    getDashboardSummary.mockResolvedValue(makeSummary());

    renderDashboard();

    await waitFor(() => {
      expect(screen.getByText("Agent One")).toBeInTheDocument();
    });

    // Agent One (unit-alpha/agent-1) should show parent unit badge
    const parentBadges = screen.getAllByTestId("agent-parent-unit");
    expect(parentBadges.length).toBeGreaterThan(0);
    expect(parentBadges[0]).toHaveTextContent("unit-alpha");
  });

  it("agent card shows role badge", async () => {
    getDashboardSummary.mockResolvedValue(makeSummary());

    renderDashboard();

    await waitFor(() => {
      expect(screen.getByText("Agent One")).toBeInTheDocument();
    });
    expect(screen.getByText("backend")).toBeInTheDocument();
    expect(screen.getByText("frontend")).toBeInTheDocument();
  });

  it("agent card shows last activity preview", async () => {
    getDashboardSummary.mockResolvedValue(
      makeSummary({
        recentActivity: [
          {
            id: "evt-1",
            source: "agent://unit-alpha/agent-1",
            eventType: "MessageReceived",
            severity: "Info",
            summary: "Processed PR review",
            timestamp: "2026-04-13T10:00:00Z",
          },
        ],
      }),
    );

    renderDashboard();

    await waitFor(() => {
      expect(screen.getByText("Agent One")).toBeInTheDocument();
    });

    const activityEl = screen.getByTestId(
      "agent-card-unit-alpha/agent-1",
    );
    expect(activityEl).toHaveTextContent("Processed PR review");
  });

  it("shows empty-state messages when data is empty", async () => {
    getDashboardSummary.mockResolvedValue(
      makeSummary({
        unitCount: 0,
        unitsByStatus: {},
        agentCount: 0,
        recentActivity: [],
        totalCost: 0,
        units: [],
        agents: [],
      }),
    );

    renderDashboard();

    await waitFor(() => {
      // Text appears as both a heading and a CTA link
      const ctaElements = screen.getAllByText("Create your first unit");
      expect(ctaElements.length).toBeGreaterThanOrEqual(1);
    });
    expect(
      screen.getByText(
        "Agents appear when you create a unit from a template.",
      ),
    ).toBeInTheDocument();
    expect(
      screen.getByText("Start a unit to see activity here."),
    ).toBeInTheDocument();
  });

  it("shows create-unit CTA link in empty units state", async () => {
    getDashboardSummary.mockResolvedValue(
      makeSummary({
        unitCount: 0,
        units: [],
      }),
    );

    renderDashboard();

    await waitFor(() => {
      expect(screen.getByTestId("create-unit-cta")).toBeInTheDocument();
    });
    expect(screen.getByTestId("create-unit-cta")).toHaveAttribute(
      "href",
      "/units/create",
    );
  });

  it("shows 'View all' link for units when units exist", async () => {
    getDashboardSummary.mockResolvedValue(makeSummary());

    renderDashboard();

    await waitFor(() => {
      expect(screen.getByText("View all")).toBeInTheDocument();
    });
    expect(screen.getByText("View all")).toHaveAttribute("href", "/units");
  });

  it("shows 'View all' link for activity when activity exists", async () => {
    getDashboardSummary.mockResolvedValue(
      makeSummary({
        recentActivity: [
          {
            id: "evt-1",
            source: "agent://agent-1",
            eventType: "MessageReceived",
            severity: "Info",
            summary: "Test event",
            timestamp: "2026-04-13T10:00:00Z",
          },
        ],
      }),
    );

    renderDashboard();

    await waitFor(() => {
      // There will be two "View all" links - one for units, one for activity
      const viewAllLinks = screen.getAllByText("View all");
      expect(viewAllLinks.length).toBe(2);
    });
    const viewAllLinks = screen.getAllByText("View all");
    expect(viewAllLinks[1]).toHaveAttribute("href", "/activity");
  });

  it("activity feed shows source badges and severity colors", async () => {
    getDashboardSummary.mockResolvedValue(
      makeSummary({
        recentActivity: [
          {
            id: "evt-1",
            source: "agent://agent-1",
            eventType: "MessageReceived",
            severity: "Info",
            summary: "Agent received a message",
            timestamp: "2026-04-13T10:00:00Z",
          },
          {
            id: "evt-2",
            source: "unit://unit-alpha",
            eventType: "StateChanged",
            severity: "Warning",
            summary: "Unit state changed",
            timestamp: "2026-04-13T09:00:00Z",
          },
        ],
      }),
    );

    renderDashboard();

    await waitFor(() => {
      expect(
        screen.getByText("Agent received a message"),
      ).toBeInTheDocument();
    });
    expect(screen.getByText("Unit state changed")).toBeInTheDocument();

    // Activity items render with test IDs
    expect(screen.getByTestId("activity-item-evt-1")).toBeInTheDocument();
    expect(screen.getByTestId("activity-item-evt-2")).toBeInTheDocument();
  });

  it("shows 'No units' health indicator when no units exist", async () => {
    getDashboardSummary.mockResolvedValue(
      makeSummary({
        unitCount: 0,
        unitsByStatus: {},
        units: [],
      }),
    );

    renderDashboard();

    await waitFor(() => {
      expect(screen.getByTestId("health-label")).toHaveTextContent("No units");
    });
  });
});
