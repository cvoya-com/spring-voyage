/**
 * Smoke tests for `@/lib/api/queries`. Verifies that:
 *   - A typed wrapper calls the underlying `api.*` method.
 *   - Results flow through TanStack Query and land on `data`.
 *   - The query key comes from `query-keys.ts` so stream invalidation
 *     can find the slice.
 */

import { renderHook, waitFor } from "@testing-library/react";
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { beforeEach, describe, expect, it, vi } from "vitest";
import type { ReactNode } from "react";

const getDashboardSummary = vi.fn();
const getUnitCost = vi.fn();

vi.mock("./client", () => ({
  api: {
    getDashboardSummary: (...args: unknown[]) =>
      getDashboardSummary(...args),
    getUnitCost: (...args: unknown[]) => getUnitCost(...args),
  },
}));

import { useDashboardSummary, useUnitCost } from "./queries";
import { queryKeys } from "./query-keys";

function wrap(client: QueryClient) {
  const Wrapper = ({ children }: { children: ReactNode }) => (
    <QueryClientProvider client={client}>{children}</QueryClientProvider>
  );
  Wrapper.displayName = "QueryWrapper";
  return Wrapper;
}

describe("queries", () => {
  beforeEach(() => {
    getDashboardSummary.mockReset();
    getUnitCost.mockReset();
  });

  it("useDashboardSummary routes through api.getDashboardSummary and uses the dashboard query key", async () => {
    const payload = {
      unitCount: 0,
      unitsByStatus: {},
      agentCount: 0,
      recentActivity: [],
      totalCost: 0,
      units: [],
      agents: [],
    };
    getDashboardSummary.mockResolvedValue(payload);

    const client = new QueryClient({
      defaultOptions: { queries: { retry: false, gcTime: 0 } },
    });
    const { result } = renderHook(() => useDashboardSummary(), {
      wrapper: wrap(client),
    });

    await waitFor(() => expect(result.current.data).toEqual(payload));
    expect(getDashboardSummary).toHaveBeenCalledOnce();

    // Cache entry exists under the documented key — this is what the
    // activity-stream hook will invalidate.
    const cached = client.getQueryData(queryKeys.dashboard.summary());
    expect(cached).toEqual(payload);
  });

  it("useUnitCost surfaces null instead of throwing when the endpoint errors", async () => {
    getUnitCost.mockRejectedValue(new Error("no cost data yet"));

    const client = new QueryClient({
      defaultOptions: { queries: { retry: false, gcTime: 0 } },
    });
    const { result } = renderHook(() => useUnitCost("eng"), {
      wrapper: wrap(client),
    });

    await waitFor(() => expect(result.current.data).toBeNull());
    expect(result.current.error).toBeNull();
  });
});
