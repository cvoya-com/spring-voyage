/**
 * Tests for `UnitDetailClient` — the `/units/[name]` scaffold (T-06,
 * issue #948). Covers:
 *
 *   - Happy-path render: unit + execution data mocked, every scaffolded
 *     fact (name / status / description / runtime / image / model)
 *     lands on the DOM.
 *   - SSE wiring: the hook is invoked with a filter that keeps only
 *     (a) events for this unit AND (b) event types in
 *     `{StateChanged, ValidationProgress}`. Non-matching unit ids AND
 *     non-matching event types are both asserted to be filtered out.
 */

import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { render, screen } from "@testing-library/react";
import type { ReactNode } from "react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

import type { ActivityEvent } from "@/lib/api/types";

// --- Mocks -------------------------------------------------------------

const useUnitMock = vi.fn();
const useUnitExecutionMock = vi.fn();

vi.mock("@/lib/api/queries", () => ({
  useUnit: (id: string) => useUnitMock(id),
  useUnitExecution: (id: string) => useUnitExecutionMock(id),
}));

// Capture the filter the component hands to `useActivityStream` so the
// SSE-wiring tests can probe it directly without booting an EventSource.
const activityStreamSpy = vi.fn();
vi.mock("@/lib/stream/use-activity-stream", () => ({
  useActivityStream: (options: unknown) => activityStreamSpy(options),
}));

// next/link mirrors the pattern used by units-page.test.tsx.
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

import UnitDetailClient from "./unit-detail-client";

function wrap(node: ReactNode) {
  const client = new QueryClient({
    defaultOptions: { queries: { retry: false, gcTime: 0 } },
  });
  return <QueryClientProvider client={client}>{node}</QueryClientProvider>;
}

function makeEvent(overrides: Partial<ActivityEvent>): ActivityEvent {
  return {
    id: "evt-1",
    timestamp: "2026-04-21T12:00:00Z",
    source: { scheme: "unit", path: "alpha" },
    eventType: "StateChanged",
    severity: "Info",
    summary: "status changed",
    ...overrides,
  };
}

describe("UnitDetailClient (T-06 scaffold)", () => {
  beforeEach(() => {
    useUnitMock.mockReset();
    useUnitExecutionMock.mockReset();
    activityStreamSpy.mockReset();
  });

  afterEach(() => {
    vi.restoreAllMocks();
  });

  // ---------------------------------------------------------------------
  // Happy-path render
  // ---------------------------------------------------------------------

  it("renders unit facts (name, status, description, runtime, image, model) once data lands", () => {
    useUnitMock.mockReturnValue({
      data: {
        id: "alpha-id",
        name: "alpha",
        displayName: "Alpha",
        description: "A unit for testing.",
        registeredAt: "2026-04-21T12:00:00Z",
        status: "Running",
        model: "claude-sonnet-4.7",
        color: null,
        tool: "claude-code",
      },
      isPending: false,
      error: null,
    });
    useUnitExecutionMock.mockReturnValue({
      data: {
        image: "ghcr.io/cvoya/claude:latest",
        runtime: "docker",
        tool: "claude-code",
        provider: "anthropic",
        model: "claude-sonnet-4.7",
      },
      isPending: false,
      error: null,
    });

    render(wrap(<UnitDetailClient name="alpha" />));

    // Name + status badge
    expect(screen.getByRole("heading", { level: 1 })).toHaveTextContent("Alpha");
    expect(screen.getByTestId("unit-detail-status")).toHaveTextContent(
      "Running",
    );
    expect(screen.getByTestId("unit-detail-name")).toHaveTextContent("alpha");

    // Description
    expect(screen.getByTestId("unit-detail-description")).toHaveTextContent(
      "A unit for testing.",
    );

    // Runtime / image / model facts
    expect(screen.getByTestId("unit-detail-runtime")).toHaveTextContent("docker");
    expect(screen.getByTestId("unit-detail-image")).toHaveTextContent(
      "ghcr.io/cvoya/claude:latest",
    );
    expect(screen.getByTestId("unit-detail-model")).toHaveTextContent(
      "claude-sonnet-4.7",
    );

    // Back link to /units
    const back = screen.getByTestId("unit-detail-back-link");
    expect(back).toHaveAttribute("href", "/units");
  });

  it("renders an empty description placeholder when the unit has no description", () => {
    useUnitMock.mockReturnValue({
      data: {
        id: "alpha-id",
        name: "alpha",
        displayName: "Alpha",
        description: "",
        registeredAt: "2026-04-21T12:00:00Z",
        status: "Draft",
        model: null,
        color: null,
      },
      isPending: false,
      error: null,
    });
    useUnitExecutionMock.mockReturnValue({
      data: { image: null, runtime: null, tool: null, provider: null, model: null },
      isPending: false,
      error: null,
    });

    render(wrap(<UnitDetailClient name="alpha" />));

    expect(
      screen.getByTestId("unit-detail-description-empty"),
    ).toBeInTheDocument();
  });

  it("shows a not-found card when the unit query returns no data", () => {
    useUnitMock.mockReturnValue({
      data: null,
      isPending: false,
      error: null,
    });
    useUnitExecutionMock.mockReturnValue({
      data: undefined,
      isPending: false,
      error: null,
    });

    render(wrap(<UnitDetailClient name="ghost" />));

    expect(screen.getByTestId("unit-detail-not-found")).toHaveTextContent(
      'Unit "ghost" not found.',
    );
  });

  // ---------------------------------------------------------------------
  // SSE wiring — filter behaviour
  // ---------------------------------------------------------------------

  describe("useActivityStream filter", () => {
    function renderAndGetFilter(
      unitName: string,
    ): (e: ActivityEvent) => boolean {
      useUnitMock.mockReturnValue({
        data: {
          id: `${unitName}-id`,
          name: unitName,
          displayName: unitName,
          description: "",
          registeredAt: "2026-04-21T12:00:00Z",
          status: "Running",
          model: null,
          color: null,
        },
        isPending: false,
        error: null,
      });
      useUnitExecutionMock.mockReturnValue({
        data: {
          image: null,
          runtime: null,
          tool: null,
          provider: null,
          model: null,
        },
        isPending: false,
        error: null,
      });

      render(wrap(<UnitDetailClient name={unitName} />));

      expect(activityStreamSpy).toHaveBeenCalled();
      const call = activityStreamSpy.mock.calls[0]?.[0] as {
        filter: (e: ActivityEvent) => boolean;
      };
      expect(call.filter).toBeInstanceOf(Function);
      return call.filter;
    }

    it("keeps StateChanged events scoped to this unit", () => {
      const filter = renderAndGetFilter("alpha");
      const kept = filter(
        makeEvent({
          source: { scheme: "unit", path: "alpha" },
          eventType: "StateChanged",
        }),
      );
      expect(kept).toBe(true);
    });

    it("keeps ValidationProgress events scoped to this unit (ready for T-05 emitter)", () => {
      const filter = renderAndGetFilter("alpha");
      // Cast through `unknown` because `ValidationProgress` is not yet in
      // the typed `ActivityEventType` union — it lands with T-02/T-05.
      // The production filter compares raw strings so the wire forms a
      // stable contract independent of the TypeScript alias.
      const kept = filter(
        makeEvent({
          source: { scheme: "unit", path: "alpha" },
          eventType: "ValidationProgress" as unknown as ActivityEvent["eventType"],
        }),
      );
      expect(kept).toBe(true);
    });

    it("drops events for a different unit id", () => {
      const filter = renderAndGetFilter("alpha");
      const kept = filter(
        makeEvent({
          source: { scheme: "unit", path: "beta" },
          eventType: "StateChanged",
        }),
      );
      expect(kept).toBe(false);
    });

    it("drops events with an irrelevant eventType even when the unit matches", () => {
      const filter = renderAndGetFilter("alpha");
      const kept = filter(
        makeEvent({
          source: { scheme: "unit", path: "alpha" },
          eventType: "MessageReceived",
        }),
      );
      expect(kept).toBe(false);
    });

    it("drops events whose source is not a unit scheme", () => {
      const filter = renderAndGetFilter("alpha");
      const kept = filter(
        makeEvent({
          source: { scheme: "agent", path: "alpha" },
          eventType: "StateChanged",
        }),
      );
      expect(kept).toBe(false);
    });
  });
});
