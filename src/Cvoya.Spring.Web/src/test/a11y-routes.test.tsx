// Route-level accessibility smoke tests for the portal (#446). Each
// top-level route listed in the sidebar renders here with its data
// dependencies stubbed, and we assert that axe-core reports no WCAG
// 2.1 AA violations. The bar is deliberately shallow — a smoke test
// per route catches regressions on the shared primitives (sidebar,
// drawer, tabs, cards, headings, form labels); richer per-component
// specs live alongside the components themselves (e.g.
// `components/ui/dialog.test.tsx`).
//
// Adding a new route: register it in the extension registry and add a
// corresponding `it("…")` below. The helper in `./a11y.ts` handles
// rule configuration; the per-route mock wiring is intentionally
// verbose so a failure points you straight at the surface-under-test.

import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { render, screen, waitFor } from "@testing-library/react";
import type { ReactNode } from "react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

import { expectNoAxeViolations } from "@/test/a11y";
import type {
  ActivityQueryResult,
  AgentResponse,
  ConversationSummary,
  DashboardSummary,
  InboxItem,
  UnitDashboardSummary,
} from "@/lib/api/types";

// ---------------------------------------------------------------------------
// Shared mocks — every test below needs a QueryClient wrapper and stubs
// for the three hooks that touch the network (EventSource for the
// activity stream, TanStack Query for REST calls, router for Link
// components).
// ---------------------------------------------------------------------------

vi.mock("@/lib/stream/use-activity-stream", () => ({
  useActivityStream: () => ({ events: [], connected: false }),
}));

// Some pages import the hook from its legacy path at `@/hooks/…`
// (the two point at the same implementation at runtime). Mock both so
// the test environment never opens a real EventSource.
vi.mock("@/hooks/use-activity-stream", () => ({
  useActivityStream: () => ({ events: [], connected: false }),
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

// next/navigation is imported by several pages. Minimal stub — the
// tests don't exercise routing.
vi.mock("next/navigation", () => ({
  useRouter: () => ({
    push: vi.fn(),
    replace: vi.fn(),
    refresh: vi.fn(),
    back: vi.fn(),
    prefetch: vi.fn(),
  }),
  usePathname: () => "/",
  useSearchParams: () => new URLSearchParams(),
  notFound: () => {
    throw new Error("notFound");
  },
  redirect: (url: string) => {
    throw new Error(`redirect:${url}`);
  },
}));

// API client surface — pages call `api.x()` through the `@/lib/api/client`
// module. We stub only the methods the exercised routes hit; anything
// else yields a rejected promise so unexpected calls surface loudly.
const apiStub = {
  getDashboardSummary: vi.fn<() => Promise<DashboardSummary>>(),
  listAgents: vi.fn<() => Promise<AgentResponse[]>>(),
  listUnits: vi.fn<() => Promise<UnitDashboardSummary[]>>(),
  getDashboardUnits: vi.fn<() => Promise<UnitDashboardSummary[]>>(),
  listConversations:
    vi.fn<() => Promise<ConversationSummary[]>>(),
  listInbox: vi.fn<() => Promise<InboxItem[]>>(),
  queryActivity:
    vi.fn<() => Promise<ActivityQueryResult>>(),
  listConnectors: vi.fn<() => Promise<unknown[]>>(),
  listAgentRuntimes: vi.fn<() => Promise<unknown[]>>(),
  getAgentRuntimeCredentialHealth: vi.fn<() => Promise<unknown | null>>(),
  getConnectorCredentialHealth: vi.fn<() => Promise<unknown | null>>(),
  listPackages: vi.fn<() => Promise<unknown[]>>(),
  searchDirectory: vi.fn<() => Promise<{ hits: unknown[]; totalCount: number }>>(),
  getTenantCost:
    vi.fn<() => Promise<{ totalCost: number; breakdowns: unknown[] }>>(),
  // Agent detail tree (#604) — the tabbed detail page fans out to
  // several panels; declaring the stubs up here keeps the mock surface
  // in one place and lets `vi.clearAllMocks()` tidy them between tests.
  getAgent: vi.fn<() => Promise<unknown>>(),
  getAgentCost: vi.fn<() => Promise<unknown>>(),
  getClones: vi.fn<() => Promise<unknown[]>>(),
  getAgentBudget: vi.fn<() => Promise<unknown>>(),
  getAgentExpertise: vi.fn<() => Promise<unknown[]>>(),
  getAgentExecution: vi.fn<() => Promise<unknown>>(),
  getUnitExecution: vi.fn<() => Promise<unknown>>(),
  getPersistentAgentDeployment: vi.fn<() => Promise<unknown>>(),
};

vi.mock("@/lib/api/client", () => ({
  api: new Proxy(apiStub, {
    get: (target, prop: string) => {
      if (prop in target) {
        // @ts-expect-error — dynamic proxy; any method we stubbed is fine.
        return () => target[prop]();
      }
      return () => Promise.reject(new Error(`Unstubbed api.${prop}`));
    },
  }),
}));

// ---------------------------------------------------------------------------
// Render helpers
// ---------------------------------------------------------------------------

function createWrapper() {
  const client = new QueryClient({
    defaultOptions: {
      queries: { retry: false, gcTime: 0, staleTime: 0 },
    },
  });
  const Wrapper = ({ children }: { children: ReactNode }) => (
    <QueryClientProvider client={client}>{children}</QueryClientProvider>
  );
  Wrapper.displayName = "A11yTestWrapper";
  return Wrapper;
}

function makeDashboardSummary(
  overrides: Partial<DashboardSummary> = {},
): DashboardSummary {
  return {
    unitCount: 2,
    unitsByStatus: { Running: 1, Draft: 1 },
    agentCount: 2,
    recentActivity: [
      {
        id: "evt-1",
        source: "agent://alpha/one",
        eventType: "MessageReceived",
        severity: "Info",
        summary: "Alpha agent received a message",
        timestamp: "2026-04-13T10:00:00Z",
      },
    ],
    totalCost: 42.5,
    units: [
      {
        name: "alpha",
        displayName: "Alpha",
        registeredAt: "2026-04-01T00:00:00Z",
        status: "Running",
      },
      {
        name: "beta",
        displayName: "Beta",
        registeredAt: "2026-04-02T00:00:00Z",
        status: "Draft",
      },
    ],
    agents: [
      {
        name: "alpha/one",
        displayName: "Agent One",
        role: "backend",
        registeredAt: "2026-04-01T00:00:00Z",
      },
      {
        name: "alpha/two",
        displayName: "Agent Two",
        role: null,
        registeredAt: "2026-04-01T00:00:00Z",
      },
    ],
    ...overrides,
  };
}

// ---------------------------------------------------------------------------
// Per-route specs
// ---------------------------------------------------------------------------

describe("portal a11y smoke tests", () => {
  beforeEach(() => {
    apiStub.getDashboardSummary.mockResolvedValue(makeDashboardSummary());
    apiStub.listAgents.mockResolvedValue([
      {
        id: "agent-1",
        name: "alpha/one",
        displayName: "Agent One",
        description: "",
        role: "backend",
        registeredAt: "2026-04-01T00:00:00Z",
        model: null,
        specialty: null,
        enabled: true,
        parentUnit: "alpha",
        executionMode: "Auto",
      } satisfies AgentResponse,
    ]);
    apiStub.listUnits.mockResolvedValue([
      {
        name: "alpha",
        displayName: "Alpha",
        registeredAt: "2026-04-01T00:00:00Z",
        status: "Running",
      },
    ]);
    apiStub.getDashboardUnits.mockResolvedValue([
      {
        name: "alpha",
        displayName: "Alpha",
        registeredAt: "2026-04-01T00:00:00Z",
        status: "Running",
      },
    ]);
    apiStub.listConversations.mockResolvedValue([]);
    apiStub.listInbox.mockResolvedValue([]);
    apiStub.queryActivity.mockResolvedValue({
      items: [],
      page: 1,
      pageSize: 20,
      totalCount: 0,
    } as unknown as ActivityQueryResult);
    apiStub.listConnectors.mockResolvedValue([]);
    apiStub.listAgentRuntimes.mockResolvedValue([]);
    apiStub.getAgentRuntimeCredentialHealth.mockResolvedValue(null);
    apiStub.getConnectorCredentialHealth.mockResolvedValue(null);
    apiStub.listPackages.mockResolvedValue([]);
    apiStub.searchDirectory.mockResolvedValue({ hits: [], totalCount: 0 });
    apiStub.getTenantCost.mockResolvedValue({ totalCost: 0, breakdowns: [] });
  });

  afterEach(() => {
    vi.clearAllMocks();
  });

  it("/ (dashboard)", async () => {
    const { default: DashboardPage } = await import("@/app/page");
    const { container } = render(<DashboardPage />, {
      wrapper: createWrapper(),
    });
    await waitFor(() => {
      expect(screen.getByText("Alpha")).toBeInTheDocument();
    });
    await expectNoAxeViolations(container);
  });

  it("/inbox", async () => {
    const { default: InboxPage } = await import("@/app/inbox/page");
    const { container } = render(<InboxPage />, {
      wrapper: createWrapper(),
    });
    // The empty state is what we rendered — wait for its heading.
    await screen.findByRole("heading", { name: /inbox/i });
    await expectNoAxeViolations(container);
  });

  it("/connectors", async () => {
    const { default: ConnectorsPage } = await import("@/app/connectors/page");
    const { container } = render(<ConnectorsPage />, {
      wrapper: createWrapper(),
    });
    await screen.findByRole("heading", { name: /connectors/i });
    await expectNoAxeViolations(container);
  });

  it("/policies (placeholder surface)", async () => {
    const { default: PoliciesPage } = await import("@/app/policies/page");
    const { container } = render(<PoliciesPage />, {
      wrapper: createWrapper(),
    });
    await screen.findByRole("heading", { name: /policies/i });
    await expectNoAxeViolations(container);
  });

  it("/agents", async () => {
    const { default: AgentsPage } = await import("@/app/agents/page");
    const { container } = render(<AgentsPage />, {
      wrapper: createWrapper(),
    });
    await screen.findByRole("heading", { level: 1, name: /agents/i });
    await expectNoAxeViolations(container);
  });

  it("/agents/[id] (tabbed detail — #604)", async () => {
    // Seed the detail response for the proxy'd `api.getAgent(…)` call so
    // the page renders past its loading skeleton and the tab list lands
    // in the axe sweep. The remaining panels reach for additional api
    // surfaces; all of them are stubbed so the detail tree settles into
    // its default Runtime tab without hitting the "Unstubbed api.…"
    // rejection path.
    apiStub.getAgent.mockResolvedValue({
      agent: {
        id: "agent-1",
        name: "alpha/one",
        displayName: "Agent One",
        description: "Primary analyst agent",
        role: "analyst",
        registeredAt: "2026-04-01T00:00:00Z",
        enabled: true,
        parentUnit: "alpha",
        executionMode: null,
      },
      deployment: null,
      status: null,
    });
    apiStub.getAgentCost.mockResolvedValue({
      totalCost: 0,
      totalInputTokens: 0,
      totalOutputTokens: 0,
      recordCount: 0,
      initiativeCost: 0,
      workCost: 0,
      breakdowns: [],
    });
    apiStub.getClones.mockResolvedValue([]);
    apiStub.getAgentBudget.mockResolvedValue(null);
    apiStub.getAgentExpertise.mockResolvedValue([]);
    apiStub.getAgentExecution.mockResolvedValue({
      image: null,
      runtime: null,
      tool: null,
      provider: null,
      model: null,
      hosting: null,
    });
    apiStub.getUnitExecution.mockResolvedValue({
      image: null,
      runtime: null,
      tool: null,
      provider: null,
      model: null,
    });
    apiStub.getPersistentAgentDeployment.mockResolvedValue({
      agentId: "alpha/one",
      running: false,
      healthStatus: "unknown",
      replicas: 0,
      image: null,
      endpoint: null,
      containerId: null,
      startedAt: null,
      consecutiveFailures: 0,
    });

    const { default: AgentDetailClient } = await import(
      "@/app/agents/[id]/agent-detail-client"
    );
    const { container } = render(<AgentDetailClient id="alpha/one" />, {
      wrapper: createWrapper(),
    });
    await screen.findByRole("heading", { level: 1, name: /agent one/i });
    // The tab list ships with `role="tablist"` — wait for it so the
    // axe sweep runs against the post-hydration tree.
    await screen.findByRole("tablist", {
      name: /agent detail sections/i,
    });
    await expectNoAxeViolations(container);
  });

  it("/units", async () => {
    const { default: UnitsPage } = await import("@/app/units/page");
    const { container } = render(<UnitsPage />, {
      wrapper: createWrapper(),
    });
    await screen.findByRole("heading", { level: 1, name: /units/i });
    await expectNoAxeViolations(container);
  });

  it("/conversations", async () => {
    const { default: ConversationsPage } = await import(
      "@/app/conversations/page"
    );
    const { container } = render(<ConversationsPage />, {
      wrapper: createWrapper(),
    });
    await screen.findByRole("heading", { level: 1, name: /conversations/i });
    await expectNoAxeViolations(container);
  });

  it("/activity", async () => {
    const { default: ActivityPage } = await import("@/app/activity/page");
    const { container } = render(<ActivityPage />, {
      wrapper: createWrapper(),
    });
    await screen.findByRole("heading", { name: /activity/i });
    await expectNoAxeViolations(container);
  });

  it("/directory", async () => {
    const { default: DirectoryPage } = await import("@/app/directory/page");
    const { container } = render(<DirectoryPage />, {
      wrapper: createWrapper(),
    });
    await screen.findByRole("heading", { name: /directory/i });
    await expectNoAxeViolations(container);
  });

  it("/packages", async () => {
    const { default: PackagesPage } = await import("@/app/packages/page");
    const { container } = render(<PackagesPage />, {
      wrapper: createWrapper(),
    });
    await screen.findByRole("heading", { name: /packages/i });
    await expectNoAxeViolations(container);
  });

  it("/initiative", async () => {
    const { default: InitiativePage } = await import(
      "@/app/initiative/page"
    );
    const { container } = render(<InitiativePage />, {
      wrapper: createWrapper(),
    });
    await screen.findByRole("heading", { level: 1, name: /initiative/i });
    await expectNoAxeViolations(container);
  });

  it("/units/create (wizard step 1)", async () => {
    const { default: CreateUnitPage } = await import(
      "@/app/units/create/page"
    );
    const { container } = render(<CreateUnitPage />, {
      wrapper: createWrapper(),
    });
    await screen.findByRole("heading", { name: /create a unit/i });
    await expectNoAxeViolations(container);
  });

  it("/analytics/costs", async () => {
    const { default: CostsPage } = await import("@/app/analytics/costs/page");
    const { container } = render(<CostsPage />, {
      wrapper: createWrapper(),
    });
    // Costs page loads asynchronously — wait for a stable heading.
    await waitFor(() => {
      expect(container.querySelector("h1, h2, h3")).toBeTruthy();
    });
    await expectNoAxeViolations(container);
  });

  it("/analytics/throughput", async () => {
    const { default: ThroughputPage } = await import(
      "@/app/analytics/throughput/page"
    );
    const { container } = render(<ThroughputPage />, {
      wrapper: createWrapper(),
    });
    await waitFor(() => {
      expect(container.querySelector("h1, h2, h3")).toBeTruthy();
    });
    await expectNoAxeViolations(container);
  });

  it("/analytics/waits", async () => {
    const { default: WaitsPage } = await import("@/app/analytics/waits/page");
    const { container } = render(<WaitsPage />, {
      wrapper: createWrapper(),
    });
    await waitFor(() => {
      expect(container.querySelector("h1, h2, h3")).toBeTruthy();
    });
    await expectNoAxeViolations(container);
  });

  it("/admin/agent-runtimes (#691)", async () => {
    const { default: AdminAgentRuntimesPage } = await import(
      "@/app/admin/agent-runtimes/page"
    );
    const { container } = render(<AdminAgentRuntimesPage />, {
      wrapper: createWrapper(),
    });
    await screen.findByRole("heading", {
      level: 1,
      name: /agent runtimes/i,
    });
    await expectNoAxeViolations(container);
  });

  it("/admin/connectors (#691)", async () => {
    const { default: AdminConnectorsPage } = await import(
      "@/app/admin/connectors/page"
    );
    const { container } = render(<AdminConnectorsPage />, {
      wrapper: createWrapper(),
    });
    await screen.findByRole("heading", {
      level: 1,
      name: /connector health/i,
    });
    await expectNoAxeViolations(container);
  });
});

// ---------------------------------------------------------------------------
// Shell + primitives — exercised standalone so their a11y properties
// stay covered even when a particular route doesn't render them.
// ---------------------------------------------------------------------------

describe("shell + primitive a11y", () => {
  it("sidebar renders with landmark nav, skip link, and aria-current on active", async () => {
    const { Sidebar } = await import("@/components/sidebar");
    const { container } = render(<Sidebar onOpenSettings={() => {}} />, {
      wrapper: createWrapper(),
    });
    // Skip link is sr-only but present in the DOM.
    expect(screen.getByTestId("skip-to-main")).toHaveAttribute(
      "href",
      "#main-content",
    );
    // `<nav aria-label="Primary">` is the main landmark.
    expect(screen.getAllByRole("navigation").length).toBeGreaterThan(0);
    await expectNoAxeViolations(container);
  });

  it("tabs primitive exposes role=tablist, tab, tabpanel with aria-selected", async () => {
    const { Tabs, TabsList, TabsTrigger, TabsContent } = await import(
      "@/components/ui/tabs"
    );
    const { container } = render(
      <Tabs defaultValue="a">
        <TabsList aria-label="Sections">
          <TabsTrigger value="a">Alpha</TabsTrigger>
          <TabsTrigger value="b">Beta</TabsTrigger>
        </TabsList>
        <TabsContent value="a">Alpha content</TabsContent>
        <TabsContent value="b">Beta content</TabsContent>
      </Tabs>,
    );
    const tabs = screen.getAllByRole("tab");
    expect(tabs).toHaveLength(2);
    expect(tabs[0]).toHaveAttribute("aria-selected", "true");
    expect(tabs[1]).toHaveAttribute("aria-selected", "false");
    expect(screen.getByRole("tabpanel")).toHaveTextContent("Alpha content");
    await expectNoAxeViolations(container);
  });

  it("dialog primitive passes axe when open", async () => {
    const { Dialog } = await import("@/components/ui/dialog");
    const { container } = render(
      <Dialog open title="Test" onClose={() => {}}>
        <p>Body copy</p>
      </Dialog>,
    );
    await screen.findByRole("dialog");
    await expectNoAxeViolations(container);
  });

  it("activity feed renders a live region for new entries", async () => {
    const { ActivityFeed } = await import("@/components/activity-feed");
    const { container } = render(
      <ActivityFeed
        items={[
          {
            id: "evt-1",
            timestamp: new Date().toISOString(),
            source: { scheme: "agent", path: "alpha/one" },
            eventType: "MessageReceived",
            severity: "Info",
            summary: "Hello there",
          },
        ]}
      />,
    );
    expect(screen.getByRole("log")).toHaveAttribute("aria-live", "polite");
    await expectNoAxeViolations(container);
  });
});
