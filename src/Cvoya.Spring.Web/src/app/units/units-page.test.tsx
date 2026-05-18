// Route-level smoke tests for the Explorer page (EXP-route, umbrella
// #815). `/units` is the canonical Explorer surface — the legacy list
// view + detail fallback are retired.

import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { fireEvent, render, screen, waitFor } from "@testing-library/react";
import type { ReactNode } from "react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

import UnitsPage from "./page";
import type { ValidatedTenantTreeNode } from "@/lib/api/validate-tenant-tree";

// Stable router mocks. Explorer node/tab changes now use native
// history.replaceState so tab clicks don't route through the App Router;
// router.replace stays spied to prove those local URL writes remain local.
const routerReplaceMock = vi.fn();
const historyReplaceStateMock = vi.fn();
let currentSearchParams = new URLSearchParams();

vi.mock("next/navigation", async () => {
  const { useSyncExternalStore } = await import("react");
  return {
    useRouter: () => ({
      push: vi.fn(),
      replace: routerReplaceMock,
      refresh: vi.fn(),
      back: vi.fn(),
      prefetch: vi.fn(),
    }),
    usePathname: () => "/units",
    useSearchParams: () =>
      useSyncExternalStore(
        (notify) => {
          subscribers.add(notify);
          return () => subscribers.delete(notify);
        },
        () => currentSearchParams,
        () => currentSearchParams,
      ),
  };
});

const subscribers = new Set<() => void>();

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

const useTenantTreeMock = vi.fn();

vi.mock("@/lib/api/queries", () => ({
  useTenantTree: () => useTenantTreeMock(),
  // The Unit Overview tab now mounts the Expertise card (#936), which
  // reads these hooks. Stub them with permanent "empty" data so the
  // Explorer page tests don't have to model expertise.
  useUnitOwnExpertise: () => ({ data: [], isPending: false }),
  useUnitAggregatedExpertise: () => ({
    data: { entries: [] },
    isPending: false,
  }),
  // The Explorer pane header now hosts `<UnitPaneActions>` (#980 item 3),
  // which reads the real UnitResponse status from `useUnit` so the
  // Validate / Start / Stop / Revalidate gate matches the server's
  // lifecycle. These smoke tests don't exercise those buttons, so we
  // stub the hook with "no data" — the Delete button is the only one
  // that always renders and the test suite doesn't click it.
  useUnit: () => ({ data: null }),
  // #2372: the detail-pane header now reads agent lifecycle through
  // `useAgent(id)`. Stub with "no data" so Explorer page tests don't
  // need to model the agent detail endpoint.
  useAgent: () => ({ data: null }),
  // Unit Overview tab (#1363) — cost timeseries sparkline. Stub with "no
  // data" so Explorer page tests don't need to model analytics.
  useUnitCostTimeseries: () => ({ data: null, isLoading: false }),
  // Unit Overview tab (#1665) — the validation panel reads the unit's
  // execution slice (image / runtime) for friendly error copy. Stub
  // with "no data" so Explorer page tests don't need to model
  // execution defaults.
  useUnitExecution: () => ({ data: null, isLoading: false }),
  // #2160: IssuesPanel hook on Unit Overview + Agent Overview. Stub
  // with "no data" so Explorer page tests don't need to model issues.
  useUnitIssues: () => ({ data: null, isPending: false, isError: false }),
  useAgentIssues: () => ({ data: null, isPending: false, isError: false }),
  // #2183: tree-explorer badge counts query. Empty so the tree
  // renders no badges in these scaffold tests.
  useIssueCounts: () => ({
    data: { counts: [] },
    isPending: false,
    isError: false,
  }),
  // Agents tab — MembershipDialog reads the model-providers catalogue (ADR-0038).
  useModelProviders: () => ({ data: [], isLoading: false }),
}));

function wrap(node: ReactNode) {
  const client = new QueryClient({
    defaultOptions: { queries: { retry: false, gcTime: 0 } },
  });
  return <QueryClientProvider client={client}>{node}</QueryClientProvider>;
}

const sampleTree: ValidatedTenantTreeNode = {
  id: "tenant://acme",
  name: "Acme",
  kind: "Tenant",
  status: "running",
  children: [
    {
      id: "engineering",
      name: "Engineering",
      kind: "Unit",
      status: "running",
      children: [
        {
          id: "ada",
          name: "Ada",
          kind: "Agent",
          status: "running",
          role: "reviewer",
          primaryParentId: "engineering",
        },
      ],
    },
    {
      id: "marketing",
      name: "Marketing",
      kind: "Unit",
      status: "paused",
    },
  ],
};

describe("UnitsPage — Explorer route (EXP-route)", () => {
  beforeEach(() => {
    routerReplaceMock.mockClear();
    historyReplaceStateMock.mockClear();
    currentSearchParams = new URLSearchParams();
    useTenantTreeMock.mockReset();
    vi.spyOn(window.history, "replaceState").mockImplementation(
      (state, title, url) => {
        historyReplaceStateMock(state, title, url);
        const target = url?.toString() ?? window.location.href;
        const qIdx = target.indexOf("?");
        const qs = qIdx >= 0 ? target.slice(qIdx + 1) : "";
        currentSearchParams = new URLSearchParams(qs);
        subscribers.forEach((fn) => fn());
      },
    );
  });
  afterEach(() => {
    subscribers.clear();
    vi.restoreAllMocks();
  });

  it("renders the loading state while the tree is fetching", () => {
    useTenantTreeMock.mockReturnValue({
      data: undefined,
      isLoading: true,
      isError: false,
    });

    render(wrap(<UnitsPage />));
    expect(screen.getByTestId("unit-explorer-loading")).toBeInTheDocument();
  });

  it("renders the error card when the tree query fails", () => {
    useTenantTreeMock.mockReturnValue({
      data: undefined,
      isLoading: false,
      isError: true,
      error: new Error("boom"),
    });

    render(wrap(<UnitsPage />));
    expect(screen.getByTestId("unit-explorer-error")).toBeInTheDocument();
    expect(screen.getByText(/boom/)).toBeInTheDocument();
  });

  it("renders the Explorer once the tree lands and defaults to the tenant root", async () => {
    useTenantTreeMock.mockReturnValue({
      data: sampleTree,
      isLoading: false,
      isError: false,
    });

    render(wrap(<UnitsPage />));
    expect(await screen.findByTestId("unit-explorer")).toBeInTheDocument();
    // Tenant root (`Tenant` kind, 5 tabs — Overview first).
    expect(screen.getByTestId("detail-tab-overview")).toHaveAttribute(
      "aria-selected",
      "true",
    );
  });

  it("respects ?node= from the URL on first render", async () => {
    currentSearchParams = new URLSearchParams("node=engineering");
    useTenantTreeMock.mockReturnValue({
      data: sampleTree,
      isLoading: false,
      isError: false,
    });

    render(wrap(<UnitsPage />));
    await screen.findByTestId("unit-explorer");
    // Engineering is a `Unit` → 10 tabs (8 visible + Config + Deployment
    // overflow, canonical-tabs.md § 7.1); first is Overview and it's active.
    expect(screen.getAllByRole("tab")).toHaveLength(10);
    expect(screen.getByTestId("detail-crumb-engineering")).toHaveAttribute(
      "aria-current",
      "page",
    );
  });

  it("writes the URL when the user picks a tree row", async () => {
    useTenantTreeMock.mockReturnValue({
      data: sampleTree,
      isLoading: false,
      isError: false,
    });
    render(wrap(<UnitsPage />));
    await screen.findByTestId("unit-explorer");

    fireEvent.click(screen.getByTestId("tree-row-engineering"));
    await waitFor(() => expect(historyReplaceStateMock).toHaveBeenCalled());
    const urlAfterSelect =
      historyReplaceStateMock.mock.calls.at(-1)?.[2]?.toString() ?? "";
    expect(urlAfterSelect).toMatch(/node=engineering/);
    expect(routerReplaceMock).not.toHaveBeenCalled();
  });

  it("#1704: clears a stale ?tab when switching to a different node kind", async () => {
    // Start with the tenant root showing and a Unit-only tab (Members)
    // in the URL from a prior navigation. We simulate this by seeding
    // the URL with the tab but NOT the node so the page renders at the
    // root (Tenant) — this avoids rendering the full Unit Members tab
    // which needs extra mocks.
    currentSearchParams = new URLSearchParams("tab=Members");
    useTenantTreeMock.mockReturnValue({
      data: sampleTree,
      isLoading: false,
      isError: false,
    });
    render(wrap(<UnitsPage />));
    await screen.findByTestId("unit-explorer");

    // Click a Unit row. The stale root-level `?tab=Members` must not
    // ride along; switching nodes without an explicit tab clears the
    // old value.
    historyReplaceStateMock.mockClear();
    fireEvent.click(screen.getByTestId("tree-row-marketing"));
    await waitFor(() => expect(historyReplaceStateMock).toHaveBeenCalled());
    const urlAfterSwitch =
      historyReplaceStateMock.mock.calls.at(-1)?.[2]?.toString() ?? "";
    // Node must update and the stale cross-kind tab must be cleared.
    expect(urlAfterSwitch).toMatch(/node=marketing/);
    expect(urlAfterSwitch).not.toMatch(/tab=/);
    expect(routerReplaceMock).not.toHaveBeenCalled();
  });

  it("renders a 'New unit' link in the page header pointing to /units/create (#1069)", async () => {
    useTenantTreeMock.mockReturnValue({
      data: sampleTree,
      isLoading: false,
      isError: false,
    });
    render(wrap(<UnitsPage />));
    await screen.findByTestId("unit-explorer");

    const link = screen.getByTestId("units-page-new-unit");
    expect(link).toBeInTheDocument();
    expect(link).toHaveAttribute("href", "/units/create");
    expect(link).toHaveTextContent(/new unit/i);
  });

  it("#2266: bounces ?node=human:<guid> to /humans/<guid> via router.replace", async () => {
    // The Explorer route honours the `human:` address scheme by
    // redirecting to the dedicated `/humans/<guid>` page. Humans don't
    // live in the tenant-tree payload, so the routing seam is the
    // narrowest unblocking surface for #2266 / #2267.
    const guid = "11111111-1111-1111-1111-111111111111";
    currentSearchParams = new URLSearchParams(`node=human:${guid}`);
    useTenantTreeMock.mockReturnValue({
      data: sampleTree,
      isLoading: false,
      isError: false,
    });
    render(wrap(<UnitsPage />));
    await waitFor(() =>
      expect(routerReplaceMock).toHaveBeenCalledWith(
        `/humans/${encodeURIComponent(guid)}`,
      ),
    );
  });

  it("#2266: preserves an active tab when bouncing ?node=human://<guid>&tab=Overview", async () => {
    const guid = "22222222-2222-2222-2222-222222222222";
    currentSearchParams = new URLSearchParams(
      `node=human://${guid}&tab=Overview`,
    );
    useTenantTreeMock.mockReturnValue({
      data: sampleTree,
      isLoading: false,
      isError: false,
    });
    render(wrap(<UnitsPage />));
    await waitFor(() =>
      expect(routerReplaceMock).toHaveBeenCalledWith(
        `/humans/${encodeURIComponent(guid)}?tab=Overview`,
      ),
    );
  });

  it("writes node+tab to the URL when a tab is clicked", async () => {
    useTenantTreeMock.mockReturnValue({
      data: sampleTree,
      isLoading: false,
      isError: false,
    });
    render(wrap(<UnitsPage />));
    await screen.findByTestId("unit-explorer");

    fireEvent.click(screen.getByTestId("detail-tab-activity"));
    await waitFor(() => expect(historyReplaceStateMock).toHaveBeenCalled());
    const last =
      historyReplaceStateMock.mock.calls.at(-1)?.[2]?.toString() ?? "";
    expect(last).toMatch(/tab=Activity/);
    expect(routerReplaceMock).not.toHaveBeenCalled();
  });
});
