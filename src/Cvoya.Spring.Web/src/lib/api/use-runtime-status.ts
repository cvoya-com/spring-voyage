"use client";

// Runtime-status polling hook (#2100). The portal renders a status chip
// next to every agent / unit name (engagement timeline, member rosters,
// drawer panels, mention chips) and we poll the dedicated cheap endpoint
// `/api/v1/tenant/{kind}/{id}/runtime-status` at ~2s cadence to keep the
// chip live without standing up a SignalR push pipeline (deferred
// follow-up — see PR body for the issue link).
//
// The hook surfaces the "best-effort" failure mode: a transient API
// hiccup leaves the chip in its previous state rather than tripping the
// page-level error boundary. The optional `enabled` flag lets callers
// suspend polling when the chip is off-screen.

import { useQuery, type UseQueryResult } from "@tanstack/react-query";

import { api } from "./client";
import { queryKeys } from "./query-keys";
import type {
  AgentRuntimeStatusResponse,
  RuntimeStatus,
} from "./types";

export type RuntimeStatusKind = "agent" | "unit";

/**
 * Default polling cadence per the issue's "sub-second is ideal; under-2s
 * is acceptable" acceptance criterion. Callers that need a different
 * cadence (e.g. a card that only needs minute-grained freshness) pass
 * `refetchIntervalMs` explicitly.
 */
export const RUNTIME_STATUS_POLL_INTERVAL_MS = 2000;

interface UseRuntimeStatusOptions {
  /** Suspend polling when the chip is hidden / off-screen. Default: true. */
  enabled?: boolean;
  /** Override the poll cadence. Defaults to {@link RUNTIME_STATUS_POLL_INTERVAL_MS}. */
  refetchIntervalMs?: number;
}

/**
 * Polls the runtime-status endpoint for an agent or unit. The
 * `data?.status` field is one of `"idle" | "busy" | "queued" |
 * "unavailable"`; the surrounding `<RuntimeStatusBadge>` projects that
 * onto a typed {@link RuntimeStatus} (which adds a `"unknown"` slot for
 * the loading-skeleton path).
 */
export function useRuntimeStatus(
  kind: RuntimeStatusKind,
  id: string | null | undefined,
  opts?: UseRuntimeStatusOptions,
): UseQueryResult<AgentRuntimeStatusResponse, Error> {
  const enabled = (opts?.enabled ?? true) && Boolean(id);
  const interval = opts?.refetchIntervalMs ?? RUNTIME_STATUS_POLL_INTERVAL_MS;

  return useQuery<AgentRuntimeStatusResponse, Error>({
    queryKey:
      kind === "agent"
        ? queryKeys.agents.runtimeStatus(id ?? "")
        : queryKeys.units.runtimeStatus(id ?? ""),
    queryFn: () => {
      if (!id) {
        // Defensive guard — `enabled` should prevent this.
        return Promise.reject(new Error("missing id"));
      }
      return kind === "agent"
        ? api.getAgentRuntimeStatus(id)
        : api.getUnitRuntimeStatus(id);
    },
    enabled,
    refetchInterval: interval,
    // Don't surface a stale-then-empty flash on an unrelated re-render.
    staleTime: interval,
    // Failures keep showing the previous data; the badge degrades to
    // `unknown` only on the very first failed fetch.
    retry: false,
  });
}

/**
 * Helper that maps a `data?.status` string from
 * {@link AgentRuntimeStatusResponse} to a typed
 * {@link RuntimeStatus}. Defaults to `"unknown"` so the chip can render
 * a placeholder while the first poll is in flight.
 */
export function projectStatus(
  raw: string | null | undefined,
): RuntimeStatus {
  switch (raw) {
    case "idle":
    case "busy":
    case "queued":
    case "unavailable":
      return raw;
    default:
      return "unknown";
  }
}
