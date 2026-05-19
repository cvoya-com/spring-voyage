import { render, screen, waitFor, act } from "@testing-library/react";
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { beforeEach, describe, expect, it, vi } from "vitest";
import type { ReactNode } from "react";

import type { DashboardSummary } from "@/lib/api/types";

// ---------------------------------------------------------------------------
// Mocks — the dashboard now reads three queries (summary, tenant-tree,
// tenant-cost) and one list (installed connectors). The SSE stream and
// router are stubbed so the page renders synchronously.
// ---------------------------------------------------------------------------

const getDashboardSummary = vi.fn<() => Promise<DashboardSummary>>();
const getTenantTree = vi.fn<() => Promise<unknown>>();
const getTenantCost =
  vi.fn<() => Promise<{ totalCost: number; breakdowns: unknown[] }>>();
const listConnectors = vi.fn<() => Promise<unknown[]>>();

vi.mock("@/lib/api/client", () => ({
  api: {
    getDashboardSummary: () => getDashboardSummary(),
    getTenantTree: () => getTenantTree(),
    getTenantCost: () => getTenantCost(),
    listConnectors: () => listConnectors(),
  },
}));

// Stub the activity stream hook so the test environment never opens a
// real EventSource.
vi.mock("@/lib/stream/use-activity-stream", () => ({
  useActivityStream: () => ({ events: [], connected: false }),
}));

// Router stub — the dashboard calls `router.push` for "Open explorer"
// and every tab-chip click. The spy lets the chip test assert the URL.
const routerPush = vi.fn();
vi.mock("next/navigation", () => ({
  useRouter: () => ({
    push: routerPush,
    replace: vi.fn(),
    refresh: vi.fn(),
    back: vi.fn(),
    prefetch: vi.fn(),
  }),
  usePathname: () => "/",
  useSearchParams: () => new URLSearchParams(),
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

/**
 * Canonical tenant-tree shape the dashboard reads. Root is a Tenant
 * with two top-level units; Unit Alpha contains an agent so the
 * widget's "kind === 'Unit'" filter is exercised.
 */
function makeTenantTree() {
  return {
    tree: {
      id: "tenant://acme",
      name: "Acme",
      kind: "Tenant",
      status: "running",
      children: [
        {
          id: "unit-alpha",
          name: "Unit Alpha",
          kind: "Unit",
          status: "running",
          children: [
            {
              id: "unit-alpha/agent-1",
              name: "Agent One",
              kind: "Agent",
              status: "running",
              role: "backend",
            },
          ],
        },
        {
          id: "unit-beta",
          name: "Unit Beta",
          kind: "Unit",
          status: "stopped",
        },
      ],
    },
  };
}

describe("DashboardPage", () => {
  beforeEach(() => {
    getDashboardSummary.mockReset();
    getTenantTree.mockReset();
    getTenantCost.mockReset();
    listConnectors.mockReset();
    routerPush.mockReset();
    // Sensible defaults so each test only restates what it exercises.
    getTenantTree.mockResolvedValue(makeTenantTree());
    getTenantCost.mockResolvedValue({ totalCost: 12.34, breakdowns: [] });
    listConnectors.mockResolvedValue([
      { typeId: "github", displayName: "GitHub" },
      { typeId: "slack", displayName: "Slack" },
    ]);
  });

  it("renders the header with title, sub-caption, and both action buttons", async () => {
    getDashboardSummary.mockResolvedValue(makeSummary());

    renderDashboard();

    await screen.findByRole("heading", { level: 1, name: /dashboard/i });
    // Sub-caption shape: "N units · M agents · K connectors healthy".
    await waitFor(() => {
      expect(screen.getByTestId("dashboard-subcaption")).toHaveTextContent(
        /2 units .* 3 agents .* 2 connectors healthy/,
      );
    });
    expect(screen.getByTestId("dashboard-copy-address")).toBeInTheDocument();
    expect(screen.getByTestId("dashboard-new-unit")).toHaveAttribute(
      "href",
      "/units/create",
    );
  });

  it("renders the 4-stat grid (Units / Agents / Running / Cost · 24h)", async () => {
    getDashboardSummary.mockResolvedValue(
      makeSummary({
        unitCount: 4,
        agentCount: 7,
        unitsByStatus: { Running: 3, Draft: 1 },
      }),
    );
    getTenantCost.mockResolvedValue({ totalCost: 8.5, breakdowns: [] });

    renderDashboard();

    await waitFor(() => {
      expect(screen.getByTestId("stats-header")).toBeInTheDocument();
    });
    const stats = screen.getByTestId("stats-header");
    expect(stats).toHaveTextContent("Units");
    expect(stats).toHaveTextContent("Agents");
    expect(stats).toHaveTextContent("Running");
    expect(stats).toHaveTextContent("Cost · 24h");
    // Numeric values.
    expect(stats).toHaveTextContent("4");
    expect(stats).toHaveTextContent("7");
    expect(stats).toHaveTextContent("3");
    await waitFor(() => {
      expect(stats).toHaveTextContent("$8.50");
    });
  });

  it("renders one UnitCard per top-level unit from the tenant tree", async () => {
    getDashboardSummary.mockResolvedValue(makeSummary());

    renderDashboard();

    await waitFor(() => {
      expect(screen.getByTestId("top-level-units-grid")).toBeInTheDocument();
    });
    // The two top-level units under the tenant render as cards; nested
    // agents under Unit Alpha do NOT render at the top level.
    expect(screen.getByTestId("unit-card-unit-alpha")).toBeInTheDocument();
    expect(screen.getByTestId("unit-card-unit-beta")).toBeInTheDocument();
    expect(
      screen.queryByTestId("unit-card-unit-alpha/agent-1"),
    ).not.toBeInTheDocument();
  });

  it("pushes /explorer when 'Open explorer' is clicked", async () => {
    getDashboardSummary.mockResolvedValue(makeSummary());

    renderDashboard();

    const openExplorer = await screen.findByTestId("open-explorer-button");
    openExplorer.click();
    expect(routerPush).toHaveBeenCalledWith("/explorer");
  });

  it("pushes /explorer/units/<id>?tab=<Tab> when a unit-card TabChip is clicked", async () => {
    getDashboardSummary.mockResolvedValue(makeSummary());

    renderDashboard();

    await waitFor(() => {
      expect(screen.getByTestId("unit-card-unit-alpha")).toBeInTheDocument();
    });
    // Grab the Activity tab chip from Unit Alpha's card. The chip lives
    // inside the card's CardTabRow footer; multiple cards render the
    // same chip name, so scope the query to a single card.
    const alpha = screen.getByTestId("unit-card-tabrow-unit-alpha");
    const activityChip = alpha.querySelector(
      '[data-testid="card-tab-chip-activity"]',
    ) as HTMLElement | null;
    expect(activityChip).not.toBeNull();
    activityChip!.click();
    expect(routerPush).toHaveBeenCalledWith(
      "/explorer/units/unit-alpha?tab=Activity",
    );
  });

  it("copies the tenant address to the clipboard when Copy is clicked", async () => {
    getDashboardSummary.mockResolvedValue(makeSummary());

    const writeText = vi.fn().mockResolvedValue(undefined);
    Object.defineProperty(navigator, "clipboard", {
      value: { writeText },
      configurable: true,
    });

    renderDashboard();

    const button = await screen.findByTestId("dashboard-copy-address");
    await act(async () => {
      button.click();
      // flush the awaited writeText promise.
      await Promise.resolve();
    });
    expect(writeText).toHaveBeenCalledWith("tenant://acme");
  });

  it("renders the activity feed with items from the summary", async () => {
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
      expect(screen.getByTestId("dashboard-activity")).toBeInTheDocument();
    });
    expect(screen.getByTestId("activity-item-evt-1")).toBeInTheDocument();
    expect(screen.getByTestId("activity-item-evt-2")).toBeInTheDocument();
    // "View all" link appears only when we have at least one item.
    expect(screen.getByText("View all")).toHaveAttribute("href", "/activity");
  });

  it("renders the bottom CostSummaryCard", async () => {
    getDashboardSummary.mockResolvedValue(makeSummary());

    renderDashboard();

    await waitFor(() => {
      expect(screen.getByTestId("cost-summary-card")).toBeInTheDocument();
    });
  });

  it("shows the empty-state CTA when there are no top-level units", async () => {
    getDashboardSummary.mockResolvedValue(
      makeSummary({
        unitCount: 0,
        unitsByStatus: {},
        units: [],
      }),
    );
    getTenantTree.mockResolvedValue({
      tree: {
        id: "tenant://acme",
        name: "Acme",
        kind: "Tenant",
        status: "running",
      },
    });

    renderDashboard();

    await waitFor(() => {
      expect(screen.getByTestId("create-unit-cta")).toBeInTheDocument();
    });
    expect(screen.getByTestId("create-unit-cta")).toHaveAttribute(
      "href",
      "/units/create",
    );
  });

  it("shows the activity empty state when there is no recent activity", async () => {
    getDashboardSummary.mockResolvedValue(makeSummary());

    renderDashboard();

    await waitFor(() => {
      expect(
        screen.getByText("Start a unit to see activity here."),
      ).toBeInTheDocument();
    });
  });

  it("ignores tenant-tree children that are agents for the top-level widget", async () => {
    // When the tenant root carries agents directly (rare but legal —
    // tenant-wide shared agents), they must NOT paint as top-level
    // "unit" cards. The filter in the dashboard keys on kind === "Unit".
    getDashboardSummary.mockResolvedValue(makeSummary());
    getTenantTree.mockResolvedValue({
      tree: {
        id: "tenant://acme",
        name: "Acme",
        kind: "Tenant",
        status: "running",
        children: [
          {
            id: "unit-alpha",
            name: "Unit Alpha",
            kind: "Unit",
            status: "running",
          },
          {
            id: "shared-agent",
            name: "Shared Agent",
            kind: "Agent",
            status: "running",
            role: "utility",
          },
        ],
      },
    });

    renderDashboard();

    await waitFor(() => {
      expect(screen.getByTestId("unit-card-unit-alpha")).toBeInTheDocument();
    });
    expect(
      screen.queryByTestId("unit-card-shared-agent"),
    ).not.toBeInTheDocument();
  });
});
