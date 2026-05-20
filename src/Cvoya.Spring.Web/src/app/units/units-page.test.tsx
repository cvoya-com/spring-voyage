// Route-level tests for `/units` (the legacy Explorer entry point) and
// `<ExplorerSurface>` (the shared Explorer canvas mounted by `/units`
// and `/explorer/units/[id]`). `/units` exists only to redirect legacy
// `?node=` URLs to the canonical path form (#2473); the Explorer canvas
// is exercised against `<ExplorerSurface>` directly so the test renders
// the same component both routes mount.

import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { fireEvent, render, screen, waitFor } from "@testing-library/react";
import type { ReactNode } from "react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

import UnitsPage from "./page";
import { ExplorerSurface } from "@/components/units/explorer-surface";
import type { ValidatedTenantTreeNode } from "@/lib/api/validate-tenant-tree";

const routerReplaceMock = vi.fn();
const historyReplaceStateMock = vi.fn();
let currentSearchParams = new URLSearchParams();
const subscribers = new Set<() => void>();

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
    usePathname: () => window.location.pathname,
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
  useUnitOwnExpertise: () => ({ data: [], isPending: false }),
  useUnitAggregatedExpertise: () => ({
    data: { entries: [] },
    isPending: false,
  }),
  useUnit: () => ({ data: null }),
  useAgent: () => ({ data: null }),
  useHuman: () => ({ data: null, isLoading: true, isError: false }),
  useCurrentUser: () => ({ data: null }),
  useUnitCostTimeseries: () => ({ data: null, isLoading: false }),
  useUnitExecution: () => ({ data: null, isLoading: false }),
  useUnitIssues: () => ({ data: null, isPending: false, isError: false }),
  useAgentIssues: () => ({ data: null, isPending: false, isError: false }),
  useThreads: () => ({ data: null, isLoading: true, isError: false }),
  useIssueCounts: () => ({
    data: { counts: [] },
    isPending: false,
    isError: false,
  }),
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

function seedUrl(pathname: string, search = "") {
  window.history.replaceState(
    null,
    "",
    search ? `${pathname}?${search}` : pathname,
  );
  currentSearchParams = new URLSearchParams(search);
}

beforeEach(() => {
  routerReplaceMock.mockClear();
  historyReplaceStateMock.mockClear();
  seedUrl("/explorer/units/");
  useTenantTreeMock.mockReset();
  vi.spyOn(window.history, "replaceState").mockImplementation(
    (state, title, url) => {
      historyReplaceStateMock(state, title, url);
      const target = url?.toString() ?? window.location.href;
      const qIdx = target.indexOf("?");
      const pathPart = qIdx >= 0 ? target.slice(0, qIdx) : target;
      const qs = qIdx >= 0 ? target.slice(qIdx + 1) : "";
      // Apply the URL change to JSDOM via the unmocked original so
      // window.location stays consistent with our assertions.
      Object.defineProperty(window, "location", {
        value: new URL(`http://localhost${pathPart}${qs ? `?${qs}` : ""}`),
        writable: true,
      });
      currentSearchParams = new URLSearchParams(qs);
      subscribers.forEach((fn) => fn());
      window.dispatchEvent(new Event("spring-voyage:explorer-url-change"));
    },
  );
});
afterEach(() => {
  subscribers.clear();
  vi.restoreAllMocks();
});

describe("UnitsPage — legacy `/units` redirect (#2473)", () => {
  it("redirects `/units?node=<id>` to `/explorer/units/<id>` via router.replace", async () => {
    seedUrl("/units", "node=engineering");
    useTenantTreeMock.mockReturnValue({
      data: sampleTree,
      isLoading: false,
      isError: false,
    });
    render(wrap(<UnitsPage />));
    await waitFor(() =>
      expect(routerReplaceMock).toHaveBeenCalledWith(
        "/explorer/units/engineering",
      ),
    );
  });

  it("strips dashes from the node id when redirecting", async () => {
    seedUrl(
      "/units",
      "node=8adb6dd4-bb7b-4998-a413-e1d1528bf71b&tab=Overview",
    );
    useTenantTreeMock.mockReturnValue({
      data: sampleTree,
      isLoading: false,
      isError: false,
    });
    render(wrap(<UnitsPage />));
    await waitFor(() =>
      expect(routerReplaceMock).toHaveBeenCalledWith(
        "/explorer/units/8adb6dd4bb7b4998a413e1d1528bf71b?tab=Overview",
      ),
    );
  });

  it("#2266: bounces ?node=human:<guid> to /humans/<guid>", async () => {
    const guid = "11111111-1111-1111-1111-111111111111";
    seedUrl("/units", `node=human:${guid}`);
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
    seedUrl("/units", `node=human://${guid}&tab=Overview`);
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
});

describe("ExplorerSurface — Explorer canvas (EXP-route)", () => {
  it("renders the loading state while the tree is fetching", () => {
    useTenantTreeMock.mockReturnValue({
      data: undefined,
      isLoading: true,
      isError: false,
    });

    render(wrap(<ExplorerSurface />));
    expect(screen.getByTestId("unit-explorer-loading")).toBeInTheDocument();
  });

  it("renders the error card when the tree query fails", () => {
    useTenantTreeMock.mockReturnValue({
      data: undefined,
      isLoading: false,
      isError: true,
      error: new Error("boom"),
    });

    render(wrap(<ExplorerSurface />));
    expect(screen.getByTestId("unit-explorer-error")).toBeInTheDocument();
    expect(screen.getByText(/boom/)).toBeInTheDocument();
  });

  it("renders the Explorer once the tree lands and defaults to the tenant root", async () => {
    useTenantTreeMock.mockReturnValue({
      data: sampleTree,
      isLoading: false,
      isError: false,
    });

    render(wrap(<ExplorerSurface />));
    expect(await screen.findByTestId("unit-explorer")).toBeInTheDocument();
    expect(screen.getByTestId("detail-tab-overview")).toHaveAttribute(
      "aria-selected",
      "true",
    );
  });

  it("respects `/explorer/units/<id>` on first render", async () => {
    seedUrl("/explorer/units/engineering");
    useTenantTreeMock.mockReturnValue({
      data: sampleTree,
      isLoading: false,
      isError: false,
    });

    render(wrap(<ExplorerSurface />));
    await screen.findByTestId("unit-explorer");
    expect(screen.getAllByRole("tab")).toHaveLength(10);
    expect(screen.getByTestId("detail-crumb-engineering")).toHaveAttribute(
      "aria-current",
      "page",
    );
  });

  it("writes a path-based URL when the user picks a tree row", async () => {
    useTenantTreeMock.mockReturnValue({
      data: sampleTree,
      isLoading: false,
      isError: false,
    });
    render(wrap(<ExplorerSurface />));
    await screen.findByTestId("unit-explorer");

    fireEvent.click(screen.getByTestId("tree-row-engineering"));
    await waitFor(() => expect(historyReplaceStateMock).toHaveBeenCalled());
    const urlAfterSelect =
      historyReplaceStateMock.mock.calls.at(-1)?.[2]?.toString() ?? "";
    expect(urlAfterSelect).toBe("/explorer/units/engineering");
    expect(routerReplaceMock).not.toHaveBeenCalled();
  });

  it("#1704: clears a stale ?tab when switching to a different node kind", async () => {
    seedUrl("/explorer/units/", "tab=Members");
    useTenantTreeMock.mockReturnValue({
      data: sampleTree,
      isLoading: false,
      isError: false,
    });
    render(wrap(<ExplorerSurface />));
    await screen.findByTestId("unit-explorer");

    historyReplaceStateMock.mockClear();
    fireEvent.click(screen.getByTestId("tree-row-marketing"));
    await waitFor(() => expect(historyReplaceStateMock).toHaveBeenCalled());
    const urlAfterSwitch =
      historyReplaceStateMock.mock.calls.at(-1)?.[2]?.toString() ?? "";
    expect(urlAfterSwitch).toBe("/explorer/units/marketing");
    expect(routerReplaceMock).not.toHaveBeenCalled();
  });

  it("renders a 'New unit' link in the page header pointing to /units/create (#1069)", async () => {
    useTenantTreeMock.mockReturnValue({
      data: sampleTree,
      isLoading: false,
      isError: false,
    });
    render(wrap(<ExplorerSurface />));
    await screen.findByTestId("unit-explorer");

    const link = screen.getByTestId("units-page-new-unit");
    expect(link).toBeInTheDocument();
    expect(link).toHaveAttribute("href", "/units/create");
    expect(link).toHaveTextContent(/new unit/i);
  });

  it("#2517: selecting a human node writes /explorer/humans/<no-dash-guid> via replaceState (no router.replace)", async () => {
    // Extend sampleTree with a human member so the tree renders a
    // clickable human row. Human node ids use the `human://<no-dash-guid>`
    // scheme — the server emits no-dash form via GuidFormatter.Format.
    const humanGuidNodash = "11111111111111111111111111111111";
    const treeWithHuman = {
      ...sampleTree,
      children: [
        ...(sampleTree.children ?? []),
        {
          id: `human://${humanGuidNodash}`,
          name: "Alice Human",
          kind: "Human" as const,
          status: "running" as const,
        },
      ],
    };
    useTenantTreeMock.mockReturnValue({
      data: treeWithHuman,
      isLoading: false,
      isError: false,
    });
    render(wrap(<ExplorerSurface />));
    await screen.findByTestId("unit-explorer");

    historyReplaceStateMock.mockClear();
    fireEvent.click(screen.getByTestId(`tree-row-human://${humanGuidNodash}`));
    await waitFor(() => expect(historyReplaceStateMock).toHaveBeenCalled());
    const urlAfterSelect =
      historyReplaceStateMock.mock.calls.at(-1)?.[2]?.toString() ?? "";
    // Human navigation writes to /explorer/humans/<no-dash-guid> — the
    // URL path segment follows the CONVENTIONS no-dash wire format (#2531).
    expect(urlAfterSelect).toBe(
      `/explorer/humans/${encodeURIComponent(humanGuidNodash)}`,
    );
    // The Explorer does NOT bounce to a separate route via router.replace.
    expect(routerReplaceMock).not.toHaveBeenCalled();
  });

  it("#2517: /explorer/humans/<no-dash-id> URL seeds the selected human into the Explorer", async () => {
    // Server emits human node ids as `human://<no-dash-guid>` via
    // GuidFormatter.Format. The canonical URL form uses the same no-dash
    // segment so selectedIdFromPathname → byId lookup matches exactly.
    const humanGuidNodash = "22222222222222222222222222222222";
    seedUrl(`/explorer/humans/${humanGuidNodash}`);
    const treeWithHuman = {
      ...sampleTree,
      children: [
        ...(sampleTree.children ?? []),
        {
          id: `human://${humanGuidNodash}`,
          name: "Bob Human",
          kind: "Human" as const,
          status: "running" as const,
        },
      ],
    };
    useTenantTreeMock.mockReturnValue({
      data: treeWithHuman,
      isLoading: false,
      isError: false,
    });
    render(wrap(<ExplorerSurface />));
    await screen.findByTestId("unit-explorer");
    // The breadcrumb for the human node should be active.
    expect(
      screen.getByTestId(`detail-crumb-human://${humanGuidNodash}`),
    ).toHaveAttribute("aria-current", "page");
  });

  it("#2531: /explorer/humans/<dashed-guid> URL still selects the correct human node (no-dash normalisation)", async () => {
    // Regression guard for #2531: a URL with a dashed UUID (e.g. a manually
    // typed link or a link generated before the no-dash fix) must still
    // resolve to the correct human node rather than falling back to the
    // tenant root. selectedIdFromPathname normalises the dashed UUID to
    // no-dash before building the `human://<id>` key for the byId lookup.
    const humanGuidDashed = "33333333-3333-3333-3333-333333333333";
    const humanGuidNodash = "33333333333333333333333333333333";
    seedUrl(`/explorer/humans/${humanGuidDashed}`);
    const treeWithHuman = {
      ...sampleTree,
      children: [
        ...(sampleTree.children ?? []),
        {
          // Server emits no-dash form — this is the byId key.
          id: `human://${humanGuidNodash}`,
          name: "Carol Human",
          kind: "Human" as const,
          status: "running" as const,
        },
      ],
    };
    useTenantTreeMock.mockReturnValue({
      data: treeWithHuman,
      isLoading: false,
      isError: false,
    });
    render(wrap(<ExplorerSurface />));
    await screen.findByTestId("unit-explorer");
    // Carol's breadcrumb must be active — not the tenant-root fallback.
    expect(
      screen.getByTestId(`detail-crumb-human://${humanGuidNodash}`),
    ).toHaveAttribute("aria-current", "page");
    // Confirm the tab catalog is Human (Overview + Messages visible = 2 visible
    // + 1 overflow = 3 tabs) not Tenant (4 visible + 1 overflow = 5 tabs).
    expect(screen.getAllByRole("tab")).toHaveLength(3);
  });

  it("#2531: /explorer/humans/<UPPERCASE-guid> URL still selects the correct human node", async () => {
    // The server emits human node ids as lowercase no-dash hex
    // (`GuidFormatter.Format`). A manually typed URL carrying an
    // uppercase UUID must be folded to lowercase before the byId key is
    // built — otherwise the lookup misses and the Explorer falls back
    // to the tenant root.
    const humanGuidUpper = "44444444-4444-4444-4444-44444444ABCD";
    const humanGuidNodash = "4444444444444444444444444444abcd";
    seedUrl(`/explorer/humans/${humanGuidUpper}`);
    const treeWithHuman = {
      ...sampleTree,
      children: [
        ...(sampleTree.children ?? []),
        {
          id: `human://${humanGuidNodash}`,
          name: "Dave Human",
          kind: "Human" as const,
          status: "running" as const,
        },
      ],
    };
    useTenantTreeMock.mockReturnValue({
      data: treeWithHuman,
      isLoading: false,
      isError: false,
    });
    render(wrap(<ExplorerSurface />));
    await screen.findByTestId("unit-explorer");
    expect(
      screen.getByTestId(`detail-crumb-human://${humanGuidNodash}`),
    ).toHaveAttribute("aria-current", "page");
    expect(screen.getAllByRole("tab")).toHaveLength(3);
  });
});
