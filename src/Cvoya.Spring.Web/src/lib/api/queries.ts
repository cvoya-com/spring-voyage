"use client";

/**
 * TanStack Query hooks over the `api` client. Every page-level fetch
 * in the portal should go through one of these so:
 *
 *   - Keys stay consistent (see `./query-keys.ts`) — `useActivityStream`
 *     uses the same keys to patch caches on SSE events (#437).
 *   - The openapi-fetch client remains the single transport.
 *   - Cache + retry + deduplication come for free.
 *
 * See `./README.md` for the one-pager on the pattern.
 */

import {
  useQuery,
  type UseQueryOptions,
  type UseQueryResult,
} from "@tanstack/react-query";

import { api } from "./client";
import { queryKeys } from "./query-keys";
import type {
  ActivityQueryResult,
  AgentDetailResponse,
  BudgetResponse,
  CloneResponse,
  CostSummaryResponse,
  DashboardSummary,
  UnitReadinessResponse,
  UnitResponse,
} from "./types";

/**
 * Options accepted by the thin wrappers below. They expose just the
 * knobs most callers need; anything more exotic (select, initialData,
 * placeholderData) can be wired in later without breaking the surface.
 */
type SliceOptions<T> = Pick<
  UseQueryOptions<T, Error>,
  "enabled" | "refetchInterval" | "staleTime"
>;

// ---------------------------------------------------------------------------
// Dashboard
// ---------------------------------------------------------------------------

export function useDashboardSummary(
  opts?: SliceOptions<DashboardSummary>,
): UseQueryResult<DashboardSummary, Error> {
  return useQuery({
    queryKey: queryKeys.dashboard.summary(),
    queryFn: () => api.getDashboardSummary(),
    ...opts,
  });
}

// ---------------------------------------------------------------------------
// Units
// ---------------------------------------------------------------------------

export function useUnit(
  id: string,
  opts?: SliceOptions<UnitResponse>,
): UseQueryResult<UnitResponse, Error> {
  return useQuery({
    queryKey: queryKeys.units.detail(id),
    queryFn: () => api.getUnit(id),
    enabled: opts?.enabled ?? Boolean(id),
    refetchInterval: opts?.refetchInterval,
    staleTime: opts?.staleTime,
  });
}

export function useUnitReadiness(
  id: string,
  opts?: SliceOptions<UnitReadinessResponse>,
): UseQueryResult<UnitReadinessResponse, Error> {
  return useQuery({
    queryKey: queryKeys.units.readiness(id),
    queryFn: () => api.getUnitReadiness(id),
    enabled: opts?.enabled ?? Boolean(id),
    refetchInterval: opts?.refetchInterval,
    staleTime: opts?.staleTime,
  });
}

export function useUnitCost(
  id: string,
  opts?: SliceOptions<CostSummaryResponse | null>,
): UseQueryResult<CostSummaryResponse | null, Error> {
  return useQuery({
    queryKey: queryKeys.units.cost(id),
    // Cost data may not exist before any activity — surface null for
    // "no data yet" instead of throwing so the UI can render the empty
    // state.
    queryFn: async () => {
      try {
        return await api.getUnitCost(id);
      } catch {
        return null;
      }
    },
    enabled: opts?.enabled ?? Boolean(id),
    refetchInterval: opts?.refetchInterval,
    staleTime: opts?.staleTime,
  });
}

// ---------------------------------------------------------------------------
// Agents
// ---------------------------------------------------------------------------

export function useAgent(
  id: string,
  opts?: SliceOptions<AgentDetailResponse>,
): UseQueryResult<AgentDetailResponse, Error> {
  return useQuery({
    queryKey: queryKeys.agents.detail(id),
    queryFn: () => api.getAgent(id),
    enabled: opts?.enabled ?? Boolean(id),
    refetchInterval: opts?.refetchInterval,
    staleTime: opts?.staleTime,
  });
}

export function useAgentCost(
  id: string,
  opts?: SliceOptions<CostSummaryResponse | null>,
): UseQueryResult<CostSummaryResponse | null, Error> {
  return useQuery({
    queryKey: queryKeys.agents.cost(id),
    queryFn: async () => {
      try {
        return await api.getAgentCost(id);
      } catch {
        return null;
      }
    },
    enabled: opts?.enabled ?? Boolean(id),
    refetchInterval: opts?.refetchInterval,
    staleTime: opts?.staleTime,
  });
}

export function useAgentClones(
  id: string,
  opts?: SliceOptions<CloneResponse[]>,
): UseQueryResult<CloneResponse[], Error> {
  return useQuery({
    queryKey: queryKeys.agents.clones(id),
    queryFn: async () => {
      try {
        return await api.getClones(id);
      } catch {
        return [];
      }
    },
    enabled: opts?.enabled ?? Boolean(id),
    refetchInterval: opts?.refetchInterval,
    staleTime: opts?.staleTime,
  });
}

export function useAgentBudget(
  id: string,
  opts?: SliceOptions<BudgetResponse | null>,
): UseQueryResult<BudgetResponse | null, Error> {
  return useQuery({
    queryKey: queryKeys.agents.budget(id),
    queryFn: async () => {
      try {
        return await api.getAgentBudget(id);
      } catch {
        // No budget set yet — surface null instead of an error.
        return null;
      }
    },
    enabled: opts?.enabled ?? Boolean(id),
    refetchInterval: opts?.refetchInterval,
    staleTime: opts?.staleTime,
  });
}

// ---------------------------------------------------------------------------
// Activity
// ---------------------------------------------------------------------------

export function useActivityQuery(
  params?: Record<string, string>,
  opts?: SliceOptions<ActivityQueryResult>,
): UseQueryResult<ActivityQueryResult, Error> {
  return useQuery({
    queryKey: queryKeys.activity.query(params),
    queryFn: async () =>
      (await api.queryActivity(params)) as ActivityQueryResult,
    ...opts,
  });
}
