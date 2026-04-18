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
const listConnectorBindings = vi.fn();
const listUnits = vi.fn();
const getUnitConnector = vi.fn();

vi.mock("./client", () => ({
  api: {
    getDashboardSummary: (...args: unknown[]) =>
      getDashboardSummary(...args),
    getUnitCost: (...args: unknown[]) => getUnitCost(...args),
    listConnectorBindings: (...args: unknown[]) =>
      listConnectorBindings(...args),
    listUnits: (...args: unknown[]) => listUnits(...args),
    getUnitConnector: (...args: unknown[]) => getUnitConnector(...args),
  },
}));

import {
  useConnectorBindings,
  useDashboardSummary,
  useUnitCost,
} from "./queries";
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
    listConnectorBindings.mockReset();
    listUnits.mockReset();
    getUnitConnector.mockReset();
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

  it("useConnectorBindings rides the bulk endpoint and never fans out per-unit (#520)", async () => {
    // Regression guard: pre-#520 the hook called api.listUnits() + N
    // api.getUnitConnector() calls. The bulk endpoint replaces the walk,
    // so both of those methods must stay untouched while
    // api.listConnectorBindings is invoked exactly once per mount.
    listConnectorBindings.mockResolvedValue([
      {
        unitId: "u1",
        unitName: "alpha",
        unitDisplayName: "Alpha",
        typeId: "github-id",
        typeSlug: "github",
        configUrl: "/api/v1/connectors/github/units/u1/config",
        actionsBaseUrl: "/api/v1/connectors/github/actions",
      },
    ]);

    const client = new QueryClient({
      defaultOptions: { queries: { retry: false, gcTime: 0 } },
    });
    const { result } = renderHook(() => useConnectorBindings("github"), {
      wrapper: wrap(client),
    });

    await waitFor(() =>
      expect(result.current.data).toEqual([
        {
          unitId: "u1",
          unitName: "alpha",
          unitDisplayName: "Alpha",
          typeId: "github-id",
          typeSlug: "github",
        },
      ]),
    );
    expect(listConnectorBindings).toHaveBeenCalledTimes(1);
    expect(listConnectorBindings).toHaveBeenCalledWith("github");
    expect(listUnits).not.toHaveBeenCalled();
    expect(getUnitConnector).not.toHaveBeenCalled();
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
