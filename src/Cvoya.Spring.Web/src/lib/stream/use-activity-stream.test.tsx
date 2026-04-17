/**
 * Tests for the activity-stream client hook.
 *
 * Covers:
 *  - Opens an EventSource against the same-origin route handler.
 *  - Invalidates the correct TanStack Query cache slices on event.
 *  - Applies the optional client-side filter before touching the
 *    cache or the local `events` array.
 *  - Tears the stream down on unmount.
 */

import { act, renderHook } from "@testing-library/react";
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import {
  afterEach,
  beforeEach,
  describe,
  expect,
  it,
  vi,
} from "vitest";
import type { ReactNode } from "react";

import { queryKeys } from "@/lib/api/query-keys";
import { useActivityStream } from "./use-activity-stream";

class MockEventSource {
  static instances: MockEventSource[] = [];
  onopen: ((ev: Event) => void) | null = null;
  onmessage: ((ev: MessageEvent) => void) | null = null;
  onerror: ((ev: Event) => void) | null = null;
  url: string;
  closed = false;

  constructor(url: string) {
    this.url = url;
    MockEventSource.instances.push(this);
  }

  close() {
    this.closed = true;
  }

  simulateMessage(data: string) {
    this.onmessage?.(new MessageEvent("message", { data }));
  }

  simulateOpen() {
    this.onopen?.(new Event("open"));
  }

  simulateError() {
    this.onerror?.(new Event("error"));
  }
}

function createWrapper(client: QueryClient) {
  const Wrapper = ({ children }: { children: ReactNode }) => (
    <QueryClientProvider client={client}>{children}</QueryClientProvider>
  );
  Wrapper.displayName = "QueryWrapper";
  return Wrapper;
}

describe("useActivityStream", () => {
  beforeEach(() => {
    MockEventSource.instances = [];
    vi.stubGlobal("EventSource", MockEventSource);
  });

  afterEach(() => {
    vi.unstubAllGlobals();
  });

  it("opens the same-origin route handler", () => {
    const client = new QueryClient();
    renderHook(() => useActivityStream(), { wrapper: createWrapper(client) });
    expect(MockEventSource.instances).toHaveLength(1);
    expect(MockEventSource.instances[0].url).toBe("/api/stream/activity");
  });

  it("does not open a stream when disabled", () => {
    const client = new QueryClient();
    renderHook(() => useActivityStream({ enabled: false }), {
      wrapper: createWrapper(client),
    });
    expect(MockEventSource.instances).toHaveLength(0);
  });

  it("invalidates agent and dashboard caches when an agent event arrives", () => {
    const client = new QueryClient();
    const invalidateSpy = vi.spyOn(client, "invalidateQueries");

    renderHook(() => useActivityStream(), { wrapper: createWrapper(client) });

    act(() => {
      MockEventSource.instances[0].simulateMessage(
        JSON.stringify({
          id: "1",
          timestamp: "2026-01-01T00:00:00Z",
          source: { scheme: "agent", path: "agent-1" },
          eventType: "MessageReceived",
          severity: "Info",
          summary: "hello",
        }),
      );
    });

    // Expect at least the four keys the source resolves to
    // (activity.all, dashboard.all, agents.detail, agents.cost).
    const queryKeysCalled = invalidateSpy.mock.calls.map(
      (call) => call[0]?.queryKey,
    );
    expect(queryKeysCalled).toEqual(
      expect.arrayContaining([
        queryKeys.activity.all,
        queryKeys.dashboard.all,
        queryKeys.agents.detail("agent-1"),
        queryKeys.agents.cost("agent-1"),
      ]),
    );
  });

  it("invalidates unit and dashboard caches when a unit event arrives", () => {
    const client = new QueryClient();
    const invalidateSpy = vi.spyOn(client, "invalidateQueries");

    renderHook(() => useActivityStream(), { wrapper: createWrapper(client) });

    act(() => {
      MockEventSource.instances[0].simulateMessage(
        JSON.stringify({
          id: "2",
          timestamp: "2026-01-01T00:00:00Z",
          source: { scheme: "unit", path: "eng" },
          eventType: "StateChanged",
          severity: "Info",
          summary: "started",
        }),
      );
    });

    const queryKeysCalled = invalidateSpy.mock.calls.map(
      (call) => call[0]?.queryKey,
    );
    expect(queryKeysCalled).toEqual(
      expect.arrayContaining([
        queryKeys.activity.all,
        queryKeys.dashboard.all,
        queryKeys.units.detail("eng"),
        queryKeys.units.cost("eng"),
      ]),
    );
  });

  it("respects the filter — dropped events neither populate `events` nor invalidate caches", () => {
    const client = new QueryClient();
    const invalidateSpy = vi.spyOn(client, "invalidateQueries");

    const { result } = renderHook(
      () =>
        useActivityStream({
          filter: (event) => event.source.path === "eng",
        }),
      { wrapper: createWrapper(client) },
    );

    act(() => {
      MockEventSource.instances[0].simulateMessage(
        JSON.stringify({
          id: "filtered-out",
          timestamp: "2026-01-01T00:00:00Z",
          source: { scheme: "unit", path: "other-unit" },
          eventType: "StateChanged",
          severity: "Info",
          summary: "skip me",
        }),
      );
    });

    expect(result.current.events).toHaveLength(0);
    expect(invalidateSpy).not.toHaveBeenCalled();

    act(() => {
      MockEventSource.instances[0].simulateMessage(
        JSON.stringify({
          id: "kept",
          timestamp: "2026-01-01T00:00:00Z",
          source: { scheme: "unit", path: "eng" },
          eventType: "StateChanged",
          severity: "Info",
          summary: "keep me",
        }),
      );
    });

    expect(result.current.events).toHaveLength(1);
    expect(result.current.events[0].summary).toBe("keep me");
    expect(invalidateSpy).toHaveBeenCalled();
  });

  it("closes the EventSource on unmount", () => {
    const client = new QueryClient();
    const { unmount } = renderHook(() => useActivityStream(), {
      wrapper: createWrapper(client),
    });
    const es = MockEventSource.instances[0];
    expect(es.closed).toBe(false);
    unmount();
    expect(es.closed).toBe(true);
  });
});
