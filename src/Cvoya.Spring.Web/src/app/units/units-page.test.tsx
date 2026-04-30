// Route-level smoke tests for the Explorer page (EXP-route, umbrella
// #815). `/units` is the canonical Explorer surface — the legacy list
// view + detail fallback are retired.

import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { fireEvent, render, screen, waitFor } from "@testing-library/react";
import type { ReactNode } from "react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

import UnitsPage from "./page";
import type { ValidatedTenantTreeNode } from "@/lib/api/validate-tenant-tree";

// Stable router mocks — router.replace is spied so we can assert URL
// updates when selection or tab changes.
const replaceMock = vi.fn();
let currentSearchParams = new URLSearchParams();

vi.mock("next/navigation", async () => {
  const { useSyncExternalStore } = await import("react");
  return {
    useRouter: () => ({
      push: vi.fn(),
      replace: (url: string) => {
        replaceMock(url);
        // Accept both `?foo=bar` and `/path?foo=bar` — the route now passes
        // the pathname alongside the query (#1039) because Next.js 16's
        // `router.replace("?…")` dropped the canonical-URL update.
        const qIdx = url.indexOf("?");
        const qs = qIdx >= 0 ? url.slice(qIdx + 1) : "";
        currentSearchParams = new URLSearchParams(qs);
        subscribers.forEach((fn) => fn());
      },
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
  // Unit Overview tab (#1363) — cost timeseries sparkline. Stub with "no
  // data" so Explorer page tests don't need to model analytics.
  useUnitCostTimeseries: () => ({ data: null, isLoading: false }),
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
    replaceMock.mockClear();
    currentSearchParams = new URLSearchParams();
    useTenantTreeMock.mockReset();
  });
  afterEach(() => {
    subscribers.clear();
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
    // Engineering is a `Unit` → 8 tabs; first is Overview and it's active.
    expect(screen.getAllByRole("tab")).toHaveLength(8);
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
    await waitFor(() => expect(replaceMock).toHaveBeenCalled());
    expect(replaceMock.mock.calls.at(-1)?.[0]).toMatch(/node=engineering/);
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

  it("writes node+tab to the URL when a tab is clicked", async () => {
    useTenantTreeMock.mockReturnValue({
      data: sampleTree,
      isLoading: false,
      isError: false,
    });
    render(wrap(<UnitsPage />));
    await screen.findByTestId("unit-explorer");

    fireEvent.click(screen.getByTestId("detail-tab-activity"));
    await waitFor(() => expect(replaceMock).toHaveBeenCalled());
    const last = replaceMock.mock.calls.at(-1)?.[0] ?? "";
    expect(last).toMatch(/tab=Activity/);
  });
});
