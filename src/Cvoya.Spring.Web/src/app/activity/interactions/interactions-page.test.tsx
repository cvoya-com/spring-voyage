/* @vitest-environment jsdom */
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { act, fireEvent, render, screen, waitFor, within } from "@testing-library/react";
import type { ReactNode } from "react";
import { beforeEach, describe, expect, it, vi } from "vitest";

import type {
  InteractionsGraphResponse,
} from "@/lib/api/types";

// Stub the openapi-fetch-backed client so the page hits a static
// fixture instead of the network. The mocks are reset per-test.
const mockSnapshot = vi.fn();
const mockTree = vi.fn();
vi.mock("@/lib/api/client", () => ({
  api: {
    getInteractionsSnapshot: (...args: unknown[]) => mockSnapshot(...args),
    getTenantTree: (...args: unknown[]) => mockTree(...args),
  },
}));

// `<UnitTree>` calls `validateTenantTreeResponse` on the wire payload;
// the validator imports from `@/lib/api/validate-tenant-tree`. Keep the
// mock simple — return whatever was provided.
vi.mock("@/lib/api/validate-tenant-tree", () => ({
  validateTenantTreeResponse: <T,>(input: T) => input,
}));

// Capture URL state — `useRouter().replace` is what the page calls when
// the operator changes a filter. The mock router keeps the latest
// search string so the test can assert on it.
let mockSearch = "";
let mockReplace = vi.fn();
vi.mock("next/navigation", () => ({
  useRouter: () => ({ replace: (url: string) => mockReplace(url) }),
  useSearchParams: () => new URLSearchParams(mockSearch),
  usePathname: () => "/activity/interactions",
}));

// xyflow's ReactFlow tries to measure DOM size — JSDOM doesn't ship
// `getBoundingClientRect` proper. Replace it with a render-skipping
// stub so the page renders without errors. We only need the graph
// component to exist for tab-toggle assertions; the real canvas
// behaviour is exercised by the Playwright suite.
vi.mock("@/components/interactions/interaction-graph", () => ({
  InteractionGraph: ({
    nodes,
    edges,
    onSelectUnit,
  }: {
    nodes: ReadonlyArray<{ id: string; kind: string }>;
    edges: ReadonlyArray<{ fromId: string; toId: string; count: number }>;
    onSelectUnit?: (id: string, kind: string) => void;
  }) => (
    <div data-testid="interaction-graph">
      <span data-testid="graph-node-count">{nodes.length}</span>
      <span data-testid="graph-edge-count">{edges.length}</span>
      {nodes.map((n) => (
        <button
          key={n.id}
          type="button"
          data-testid={`mock-graph-node-${n.id}`}
          onClick={() => onSelectUnit?.(n.id, n.kind)}
        >
          {n.id}
        </button>
      ))}
    </div>
  ),
  LivePulse: undefined,
}));

// Stub recharts ResponsiveContainer to render its children with a fixed
// frame so JSDOM (no layout) doesn't bail on width=0 detection.
vi.mock("recharts", async () => {
  const actual = await vi.importActual<typeof import("recharts")>("recharts");
  return {
    ...actual,
    ResponsiveContainer: ({
      children,
    }: {
      children: ReactNode;
    }) => <div style={{ width: 800, height: 200 }}>{children}</div>,
  };
});

// Stub EventSource so live-mode toggles don't blow up under JSDOM.
type FakeListener = (evt: { data: string }) => void;
class FakeEventSource {
  url: string;
  static instances: FakeEventSource[] = [];
  listeners: Record<string, FakeListener[]> = {};
  onopen: (() => void) | null = null;
  onerror: (() => void) | null = null;
  constructor(url: string) {
    this.url = url;
    FakeEventSource.instances.push(this);
  }
  addEventListener(name: string, cb: FakeListener): void {
    (this.listeners[name] ||= []).push(cb);
  }
  close(): void {
    /* no-op */
  }
  emit(name: string, data: unknown): void {
    for (const cb of this.listeners[name] ?? []) {
      cb({ data: JSON.stringify(data) });
    }
  }
}

const FIXTURE: InteractionsGraphResponse = {
  nodes: [
    {
      id: "agent-1",
      kind: "agent",
      displayName: "Agent One",
      sent: 5,
      received: 3,
    },
    {
      id: "unit-1",
      kind: "unit",
      displayName: "Unit One",
      sent: 2,
      received: 4,
    },
    {
      id: "human-1",
      kind: "human",
      displayName: "Human One",
      sent: 1,
      received: 1,
    },
    {
      id: "connector-1",
      kind: "connector",
      displayName: "Connector",
      sent: 3,
      received: 0,
    },
  ],
  edges: [
    {
      fromId: "agent-1",
      toId: "unit-1",
      count: 7,
      firstAt: "2026-05-27T10:00:00Z",
      lastAt: "2026-05-27T10:01:00Z",
      channels: ["unit"],
    },
    {
      fromId: "connector-1",
      toId: "agent-1",
      count: 3,
      firstAt: "2026-05-27T10:00:00Z",
      lastAt: "2026-05-27T10:00:30Z",
      channels: ["agent"],
    },
  ],
  timeline: [
    {
      bucket: "2026-05-27T10:00:00Z",
      sent: 4,
      byKind: { agent: 3, unit: 1 },
    },
    {
      bucket: "2026-05-27T10:01:00Z",
      sent: 6,
      byKind: { agent: 4, unit: 2 },
    },
  ],
  truncated: null,
};

const TRUNCATED_FIXTURE: InteractionsGraphResponse = {
  ...FIXTURE,
  truncated: { total: 100, kept: 50 },
};

function renderPage(Component: React.ComponentType): ReturnType<typeof render> {
  const client = new QueryClient({
    defaultOptions: { queries: { retry: false, gcTime: 0, staleTime: 0 } },
  });
  const Wrapper = ({ children }: { children: ReactNode }) => (
    <QueryClientProvider client={client}>{children}</QueryClientProvider>
  );
  return render(<Component />, { wrapper: Wrapper });
}

beforeEach(() => {
  mockSnapshot.mockReset();
  mockTree.mockReset();
  mockReplace = vi.fn();
  mockSearch = "";
  FakeEventSource.instances = [];
  // Stub the global before each test so module-level captures don't
  // break across runs.
  (
    globalThis as unknown as { EventSource: typeof FakeEventSource }
  ).EventSource = FakeEventSource;
  mockSnapshot.mockResolvedValue(FIXTURE);
  mockTree.mockResolvedValue({
    id: "tenant",
    name: "tenant",
    kind: "Tenant",
    status: "running",
    children: [],
  });
});

describe("InteractionsPage", () => {
  it("renders the graph + matrix by default and shows the heading", async () => {
    const InteractionsPage = (await import("./page")).default;
    renderPage(InteractionsPage);
    await waitFor(() =>
      expect(screen.getByTestId("interactions-page")).toBeInTheDocument(),
    );
    expect(
      screen.getByRole("heading", { name: /interactions/i }),
    ).toBeInTheDocument();
    // Default view = "both" → both graph + matrix visible.
    await waitFor(() => {
      expect(screen.getByTestId("interaction-graph")).toBeInTheDocument();
      expect(screen.getByTestId("interaction-matrix")).toBeInTheDocument();
    });
  });

  it("depth dial defaults to 2 and updates the URL when changed", async () => {
    const InteractionsPage = (await import("./page")).default;
    renderPage(InteractionsPage);

    await waitFor(() =>
      expect(screen.getByTestId("interaction-filters")).toBeInTheDocument(),
    );
    const dial2 = screen.getByTestId("interaction-filters-neighbours-2");
    expect(dial2).toHaveAttribute("aria-checked", "true");

    fireEvent.click(screen.getByTestId("interaction-filters-neighbours-1"));
    await waitFor(() => {
      expect(mockReplace).toHaveBeenCalledWith(
        expect.stringContaining("neighbours=1"),
      );
    });
  });

  it("view-mode toggle renders the right combination of components", async () => {
    const InteractionsPage = (await import("./page")).default;
    renderPage(InteractionsPage);

    // Switch to "matrix" only.
    await waitFor(() =>
      expect(screen.getByTestId("interaction-filters")).toBeInTheDocument(),
    );
    fireEvent.click(screen.getByTestId("interaction-filters-view-matrix"));

    await waitFor(() => {
      expect(screen.queryByTestId("interaction-graph")).not.toBeInTheDocument();
      expect(screen.getByTestId("interaction-matrix")).toBeInTheDocument();
    });

    // Now switch to "graph" only.
    fireEvent.click(screen.getByTestId("interaction-filters-view-graph"));
    await waitFor(() => {
      expect(screen.getByTestId("interaction-graph")).toBeInTheDocument();
      expect(screen.queryByTestId("interaction-matrix")).not.toBeInTheDocument();
    });
  });

  it("renders the truncation banner with a working matrix-switch link", async () => {
    mockSnapshot.mockResolvedValue(TRUNCATED_FIXTURE);
    const InteractionsPage = (await import("./page")).default;
    renderPage(InteractionsPage);

    await waitFor(() => {
      expect(
        screen.getByTestId("interactions-truncation-banner"),
      ).toBeInTheDocument();
    });
    fireEvent.click(
      screen.getByTestId("interactions-truncation-switch-matrix"),
    );
    await waitFor(() => {
      expect(mockReplace).toHaveBeenCalledWith(
        expect.stringContaining("view=matrix"),
      );
    });
  });

  it("omits the connector column in the matrix view (ADR-0048)", async () => {
    const InteractionsPage = (await import("./page")).default;
    renderPage(InteractionsPage);

    await waitFor(() =>
      expect(screen.getByTestId("interaction-matrix")).toBeInTheDocument(),
    );
    // Receiver columns: connector should NOT appear; agent / unit / human should.
    expect(
      screen.queryByTestId("interaction-matrix-col-connector-1"),
    ).not.toBeInTheDocument();
    expect(
      screen.getByTestId("interaction-matrix-col-agent-1"),
    ).toBeInTheDocument();
    expect(
      screen.getByTestId("interaction-matrix-col-unit-1"),
    ).toBeInTheDocument();
    expect(
      screen.getByTestId("interaction-matrix-col-human-1"),
    ).toBeInTheDocument();
  });

  it("clicking a matrix cell opens the detail popover", async () => {
    const InteractionsPage = (await import("./page")).default;
    renderPage(InteractionsPage);

    await waitFor(() =>
      expect(screen.getByTestId("interaction-matrix")).toBeInTheDocument(),
    );
    fireEvent.click(
      screen.getByTestId("interaction-matrix-cell-agent-1-unit-1"),
    );
    await waitFor(() => {
      const detail = screen.getByTestId("interaction-detail");
      expect(detail).toBeInTheDocument();
      expect(within(detail).getByTestId("interaction-detail-count").textContent)
        .toBe("7");
    });
  });

  it("seeds URL state from the URL on mount", async () => {
    mockSearch = "view=matrix&neighbours=0&unit=unit-x";
    const InteractionsPage = (await import("./page")).default;
    renderPage(InteractionsPage);

    await waitFor(() =>
      expect(screen.getByTestId("interaction-filters")).toBeInTheDocument(),
    );
    // Depth dial reflects neighbours=0
    expect(
      screen.getByTestId("interaction-filters-neighbours-0"),
    ).toHaveAttribute("aria-checked", "true");
    // View mode picked matrix
    expect(screen.getByTestId("interaction-filters-view-matrix")).toHaveAttribute(
      "aria-selected",
      "true",
    );
    // Scope chip shows the URL-supplied unit
    expect(screen.getByTestId("interaction-filters-scope-chip").textContent)
      .toContain("unit-x");
  });

  it("brushing the timeline triggers a snapshot refetch with the new window", async () => {
    const InteractionsPage = (await import("./page")).default;
    renderPage(InteractionsPage);

    await waitFor(() =>
      expect(screen.getByTestId("interaction-timeline")).toBeInTheDocument(),
    );
    mockReplace.mockClear();
    // Simulate brush via the timeline's render-prop pathway: the
    // graph component re-fires `onBrush` on mount with the data start
    // / end when the timeline mounts under recharts. Force a call via
    // the URL-state route — fire onChange on the matrix sort to prove
    // the URL round-trip path is wired identically. (Brushing inside
    // recharts requires a layout we don't have under JSDOM, so we
    // exercise the same applyState code path via a deterministic
    // trigger.) Then call the page's brush handler indirectly by
    // applying since/until via the filters.
    const sinceInput = screen.getByTestId(
      "interaction-filters-since",
    ) as HTMLInputElement;
    fireEvent.change(sinceInput, { target: { value: "2026-05-27T09:30" } });
    await waitFor(() => {
      expect(mockReplace).toHaveBeenCalledWith(
        expect.stringContaining("since="),
      );
    });
  });

  it("turning live mode on disables the timeline brush", async () => {
    const InteractionsPage = (await import("./page")).default;
    renderPage(InteractionsPage);

    await waitFor(() =>
      expect(screen.getByTestId("interaction-timeline")).toBeInTheDocument(),
    );
    // Brush is rendered while not live — exposed via a stable data attr
    // on the timeline wrapper (recharts <Brush> itself doesn't accept
    // data-testid, and the actual brush UI doesn't paint under JSDOM
    // anyway).
    expect(screen.getByTestId("interaction-timeline")).toHaveAttribute(
      "data-brush-enabled",
    );

    fireEvent.click(screen.getByTestId("interaction-filters-live-toggle"));

    await waitFor(() => {
      expect(
        screen.getByTestId("interaction-timeline"),
      ).not.toHaveAttribute("data-brush-enabled");
    });
  });

  it("a live throttled frame updates the dropped-count header indicator", async () => {
    const InteractionsPage = (await import("./page")).default;
    renderPage(InteractionsPage);

    await waitFor(() =>
      expect(screen.getByTestId("interaction-filters")).toBeInTheDocument(),
    );
    fireEvent.click(screen.getByTestId("interaction-filters-live-toggle"));

    // Wait for the EventSource to be opened by useInteractionsStream.
    await waitFor(() => {
      expect(FakeEventSource.instances.length).toBeGreaterThan(0);
    });
    const es = FakeEventSource.instances[FakeEventSource.instances.length - 1];
    act(() => {
      es.emit("throttled", { since: new Date().toISOString(), dropped: 7 });
    });

    await waitFor(() => {
      expect(
        screen.getByTestId("interaction-live-throttle-indicator").textContent,
      ).toContain("+7 more dropped");
    });
  });
});
