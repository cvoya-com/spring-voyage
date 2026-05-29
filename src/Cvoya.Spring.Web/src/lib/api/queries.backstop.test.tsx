/**
 * #2771 — live-refresh backstops.
 *
 * The portal's "Live" SSE relay runs in `spring-api` and cannot see the
 * activity events that worker-side actors emit (the activity bus is
 * process-local; see ADR-0052). Until the cross-host stream bridge ships
 * (#2896) the conversation, activity, and analytics queries carry a
 * `refetchInterval` so new data appears without a remount.
 *
 * These tests capture the options each wrapper hands to `useQuery` and
 * assert the backstop interval is wired — mirroring the
 * `overview-tab.test.tsx` "inspect the refetchInterval option" pattern.
 * `@tanstack/react-query` is mocked so the assertion is deterministic and
 * timer-free; the api client is mocked because the captured `queryFn`
 * closures are never invoked under the mock.
 */

import { renderHook } from "@testing-library/react";
import { beforeEach, describe, expect, it, vi } from "vitest";

const useQuerySpy = vi.fn();

vi.mock("@tanstack/react-query", () => ({
  useQuery: (options: unknown) => {
    useQuerySpy(options);
    return { data: undefined, isPending: true, isFetching: true, error: null };
  },
  useMutation: () => ({ mutate: vi.fn(), mutateAsync: vi.fn() }),
  useQueryClient: () => ({
    invalidateQueries: vi.fn(),
    cancelQueries: vi.fn(),
    getQueryData: vi.fn(),
    setQueryData: vi.fn(),
  }),
}));

vi.mock("./client", () => ({ api: {} }));

import {
  ANALYTICS_BACKSTOP_REFETCH_INTERVAL_MS,
  LIVE_BACKSTOP_REFETCH_INTERVAL_MS,
  useActivityQuery,
  useAnalyticsThroughput,
  useAnalyticsWaits,
  useConversation,
  useThread,
} from "./queries";

interface CapturedOptions {
  refetchInterval?: unknown;
  refetchOnWindowFocus?: unknown;
}

function lastOptions(): CapturedOptions {
  const { calls } = useQuerySpy.mock;
  expect(calls.length).toBeGreaterThan(0);
  return calls[calls.length - 1][0] as CapturedOptions;
}

describe("query live-refresh backstops (#2771)", () => {
  beforeEach(() => {
    useQuerySpy.mockClear();
  });

  it("uses a faster live tier than the analytics tier", () => {
    // Sanity guard so the two constants don't accidentally converge.
    expect(LIVE_BACKSTOP_REFETCH_INTERVAL_MS).toBeGreaterThan(0);
    expect(ANALYTICS_BACKSTOP_REFETCH_INTERVAL_MS).toBeGreaterThan(
      LIVE_BACKSTOP_REFETCH_INTERVAL_MS,
    );
  });

  it("useThread backstops the thread detail at the live interval", () => {
    renderHook(() => useThread("thread-1"));
    const opts = lastOptions();
    expect(opts.refetchInterval).toBe(LIVE_BACKSTOP_REFETCH_INTERVAL_MS);
    expect(opts.refetchOnWindowFocus).toBe(true);
  });

  it("useConversation backstops the observed thread at the live interval", () => {
    renderHook(() => useConversation("thread-1"));
    expect(lastOptions().refetchInterval).toBe(
      LIVE_BACKSTOP_REFETCH_INTERVAL_MS,
    );
  });

  it("useActivityQuery backstops the activity feed at the live interval", () => {
    renderHook(() => useActivityQuery({ pageSize: "20" }));
    expect(lastOptions().refetchInterval).toBe(
      LIVE_BACKSTOP_REFETCH_INTERVAL_MS,
    );
  });

  it("analytics rollups backstop at the slower analytics interval", () => {
    renderHook(() =>
      useAnalyticsThroughput({ from: "2026-01-01", to: "2026-01-02" }),
    );
    expect(lastOptions().refetchInterval).toBe(
      ANALYTICS_BACKSTOP_REFETCH_INTERVAL_MS,
    );

    useQuerySpy.mockClear();
    renderHook(() =>
      useAnalyticsWaits({ from: "2026-01-01", to: "2026-01-02" }),
    );
    expect(lastOptions().refetchInterval).toBe(
      ANALYTICS_BACKSTOP_REFETCH_INTERVAL_MS,
    );
  });

  it("lets a caller-supplied refetchInterval override the backstop default", () => {
    // The interactions-history hook (rewind mode) relies on this to opt
    // out with `false`; a numeric override must win too.
    renderHook(() => useThread("thread-1", { refetchInterval: 999 }));
    expect(lastOptions().refetchInterval).toBe(999);
  });
});
