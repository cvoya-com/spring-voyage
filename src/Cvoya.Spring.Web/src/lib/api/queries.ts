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
  AgentDashboardSummary,
  AgentDetailResponse,
  AgentResponse,
  AggregatedExpertiseResponse,
  BudgetResponse,
  CloneResponse,
  ConnectorTypeResponse,
  ConversationDetail,
  ConversationListFilters,
  ConversationSummary,
  CostDashboardSummary,
  CostSummaryResponse,
  DashboardSummary,
  ExpertiseDomainDto,
  InboxItem,
  InitiativeLevelResponse,
  InitiativePolicy,
  PackageDetail,
  PackageSummary,
  PersistentAgentDeploymentResponse,
  PersistentAgentLogsResponse,
  PlatformInfoResponse,
  ThroughputRollupResponse,
  TokenResponse,
  UnitBoundaryResponse,
  UnitDashboardSummary,
  UnitDetailResponse,
  UnitOrchestrationResponse,
  UnitPolicyResponse,
  UnitReadinessResponse,
  UnitResponse,
  UnitTemplateDetail,
  UnitTemplateSummary,
  UserProfileResponse,
  WaitTimeRollupResponse,
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

export function useDashboardAgents(
  opts?: SliceOptions<AgentDashboardSummary[]>,
): UseQueryResult<AgentDashboardSummary[], Error> {
  return useQuery({
    queryKey: queryKeys.dashboard.agents(),
    queryFn: () => api.getDashboardAgents(),
    ...opts,
  });
}

export function useDashboardUnits(
  opts?: SliceOptions<UnitDashboardSummary[]>,
): UseQueryResult<UnitDashboardSummary[], Error> {
  return useQuery({
    queryKey: queryKeys.dashboard.units(),
    queryFn: () => api.getDashboardUnits(),
    ...opts,
  });
}

export function useDashboardCosts(
  opts?: SliceOptions<CostDashboardSummary>,
): UseQueryResult<CostDashboardSummary, Error> {
  return useQuery({
    queryKey: queryKeys.dashboard.costs(),
    queryFn: () => api.getDashboardCosts(),
    ...opts,
  });
}

/**
 * Tenant-wide cost rollup for an explicit `(from, to)` window. Powers
 * the dashboard summary card's today / 7d / 30d totals (PR-R4, #394).
 * Surfaces `null` on error so the card renders the empty slot instead
 * of trapping the dashboard error boundary.
 */
export function useTenantCost(
  range: { from: string; to: string },
  opts?: SliceOptions<CostSummaryResponse | null>,
): UseQueryResult<CostSummaryResponse | null, Error> {
  return useQuery({
    queryKey: queryKeys.tenant.cost(range.from, range.to),
    queryFn: async () => {
      try {
        return await api.getTenantCost(range);
      } catch {
        return null;
      }
    },
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

export function useUnitDetail(
  id: string,
  opts?: SliceOptions<UnitDetailResponse>,
): UseQueryResult<UnitDetailResponse, Error> {
  return useQuery({
    queryKey: queryKeys.units.fullDetail(id),
    queryFn: () => api.getUnitDetail(id),
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

/**
 * Unified unit policy read covering all five dimensions (skill / model
 * / cost / executionMode / initiative) plus the forthcoming label
 * routing slot. Mirrors `spring unit policy <dim> get` (#453) so the
 * CLI and portal round-trip the same shape.
 *
 * Surfaces an empty `{}` rather than throwing when the server returns
 * a 404 (policy never set) so the Policies tab can render the empty
 * "(none — no constraint on this unit)" state without trapping the
 * error boundary.
 */
export function useUnitPolicy(
  id: string,
  opts?: SliceOptions<UnitPolicyResponse>,
): UseQueryResult<UnitPolicyResponse, Error> {
  return useQuery({
    queryKey: queryKeys.units.policy(id),
    queryFn: async () => {
      try {
        return await api.getUnitPolicy(id);
      } catch {
        return {} as UnitPolicyResponse;
      }
    },
    enabled: opts?.enabled ?? Boolean(id),
    refetchInterval: opts?.refetchInterval,
    staleTime: opts?.staleTime,
  });
}

/**
 * Read a unit's boundary configuration (#413). The endpoint always
 * returns the empty shape on unset, so the hook never needs to branch
 * on "no boundary yet".
 */
export function useUnitBoundary(
  id: string,
  opts?: SliceOptions<UnitBoundaryResponse>,
): UseQueryResult<UnitBoundaryResponse, Error> {
  return useQuery({
    queryKey: queryKeys.units.boundary(id),
    queryFn: () => api.getUnitBoundary(id),
    enabled: opts?.enabled ?? Boolean(id),
    refetchInterval: opts?.refetchInterval,
    staleTime: opts?.staleTime,
  });
}

/**
 * Read a unit's orchestration strategy slot (#606). Always returns the
 * empty shape on unset (the server never 404s for a live unit), so the
 * Orchestration tab renders the "(unset — inferred)" state without a
 * branch.
 */
export function useUnitOrchestration(
  id: string,
  opts?: SliceOptions<UnitOrchestrationResponse>,
): UseQueryResult<UnitOrchestrationResponse, Error> {
  return useQuery({
    queryKey: queryKeys.units.orchestration(id),
    queryFn: () => api.getUnitOrchestration(id),
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

/**
 * Windowed wrapper around `api.getUnitCost`. Keyed on `(id, from, to)`
 * so the Costs tab on `/units/[id]` (PR-R4, #394) can fan out one call
 * per window (24h / 7d / 30d) without colliding with the default-window
 * `useUnitCost` above.
 */
export function useUnitCostWindowed(
  id: string,
  range: { from: string; to: string },
  opts?: SliceOptions<CostSummaryResponse | null>,
): UseQueryResult<CostSummaryResponse | null, Error> {
  return useQuery({
    queryKey: [...queryKeys.units.cost(id), range.from, range.to] as const,
    queryFn: async () => {
      try {
        return await api.getUnitCost(id, range);
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

/**
 * Windowed wrapper around `api.getAgentCost`. Keyed on `(id, from, to)`
 * so the cost-over-time card on `/agents/[id]` (PR-R4, #394) can fan
 * out one call per window (24h / 7d / 30d). The API does not expose a
 * time-series today (#569) — the card stacks per-window totals as a
 * best-available placeholder until that endpoint lands.
 */
export function useAgentCostWindowed(
  id: string,
  range: { from: string; to: string },
  opts?: SliceOptions<CostSummaryResponse | null>,
): UseQueryResult<CostSummaryResponse | null, Error> {
  return useQuery({
    queryKey: [...queryKeys.agents.cost(id), range.from, range.to] as const,
    queryFn: async () => {
      try {
        return await api.getAgentCost(id, range);
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

export function useAgentInitiativeLevel(
  id: string,
  opts?: SliceOptions<InitiativeLevelResponse | null>,
): UseQueryResult<InitiativeLevelResponse | null, Error> {
  return useQuery({
    queryKey: queryKeys.agents.initiativeLevel(id),
    // Surface null for "level not yet known" so callers don't have to
    // special-case the first-load failure.
    queryFn: async () => {
      try {
        return await api.getAgentInitiativeLevel(id);
      } catch {
        return null;
      }
    },
    enabled: opts?.enabled ?? Boolean(id),
    refetchInterval: opts?.refetchInterval,
    staleTime: opts?.staleTime,
  });
}

/**
 * Current persistent-agent deployment state (#396). Returns the canonical
 * "not running" shape (Running=false, HealthStatus="unknown", Replicas=0)
 * when no deployment is tracked — the server's `GET /deployment` already
 * normalises this, so callers don't need to special-case empty state.
 * Ephemeral agents never yield a non-empty response; the lifecycle UI
 * still renders so the verbs stay reachable per UI/CLI parity, and the
 * mutation handlers surface the server's 400 verbatim.
 */
export function useAgentDeployment(
  id: string,
  opts?: SliceOptions<PersistentAgentDeploymentResponse | null>,
): UseQueryResult<PersistentAgentDeploymentResponse | null, Error> {
  return useQuery({
    queryKey: queryKeys.agents.deployment(id),
    queryFn: async () => {
      try {
        return await api.getPersistentAgentDeployment(id);
      } catch {
        // A 404 here means the agent itself was removed. Surface null so
        // the tab can render the empty state rather than bubbling the
        // error up through an error boundary.
        return null;
      }
    },
    enabled: opts?.enabled ?? Boolean(id),
    refetchInterval: opts?.refetchInterval,
    staleTime: opts?.staleTime,
  });
}

/**
 * Snapshot of the persistent-agent container logs. Mirrors
 * `spring agent logs <id> --tail <n>`. The `tail` knob is part of the
 * query key so two tabs open on different tail windows don't collide.
 * A streaming upgrade is tracked as a follow-up — today this is a
 * manual-refresh snapshot, consistent with the CLI.
 */
export function useAgentLogs(
  id: string,
  tail: number,
  opts?: SliceOptions<PersistentAgentLogsResponse | null>,
): UseQueryResult<PersistentAgentLogsResponse | null, Error> {
  return useQuery({
    queryKey: queryKeys.agents.logs(id, tail),
    queryFn: async () => {
      try {
        return await api.getPersistentAgentLogs(id, tail);
      } catch {
        // Agent exists but no container deployment — the server returns
        // 404 for "not deployed". Surface null so the UI can render a
        // clean "deploy first" state instead of the error boundary.
        return null;
      }
    },
    enabled: opts?.enabled ?? Boolean(id),
    refetchInterval: opts?.refetchInterval,
    staleTime: opts?.staleTime,
  });
}

export function useAgentInitiativePolicy(
  id: string,
  opts?: SliceOptions<InitiativePolicy | null>,
): UseQueryResult<InitiativePolicy | null, Error> {
  return useQuery({
    queryKey: queryKeys.agents.initiativePolicy(id),
    queryFn: async () => {
      try {
        return (await api.getAgentInitiativePolicy(id)) as InitiativePolicy;
      } catch {
        // No policy set yet — surface null rather than throwing so the
        // UI can render the "use defaults" empty state.
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

// ---------------------------------------------------------------------------
// Analytics (#448 / #457)
// ---------------------------------------------------------------------------
//
// Each hook takes the resolved `(from, to)` window plus an optional source
// filter. The hooks are the TanStack Query surface the three
// `/analytics/*` pages ride on; CLI parity is kept by mirroring the
// `spring analytics {throughput,waits}` flags 1:1 (--window, --unit,
// --agent) → the same wire contract.

export interface AnalyticsRangeArgs {
  /** Optional `scheme://name` substring filter; matches the CLI `--unit` / `--agent` flags. */
  source?: string;
  /** ISO start of the rollup window. */
  from: string;
  /** ISO end of the rollup window. */
  to: string;
}

export function useAnalyticsThroughput(
  args: AnalyticsRangeArgs,
  opts?: SliceOptions<ThroughputRollupResponse>,
): UseQueryResult<ThroughputRollupResponse, Error> {
  return useQuery({
    queryKey: queryKeys.analytics.throughput({
      source: args.source,
      from: args.from,
      to: args.to,
    }),
    queryFn: () =>
      api.getAnalyticsThroughput({
        source: args.source,
        from: args.from,
        to: args.to,
      }) as Promise<ThroughputRollupResponse>,
    ...opts,
  });
}

export function useAnalyticsWaits(
  args: AnalyticsRangeArgs,
  opts?: SliceOptions<WaitTimeRollupResponse>,
): UseQueryResult<WaitTimeRollupResponse, Error> {
  return useQuery({
    queryKey: queryKeys.analytics.waits({
      source: args.source,
      from: args.from,
      to: args.to,
    }),
    queryFn: () =>
      api.getAnalyticsWaits({
        source: args.source,
        from: args.from,
        to: args.to,
      }) as Promise<WaitTimeRollupResponse>,
    ...opts,
  });
}

// ---------------------------------------------------------------------------
// Conversations (#410)
// ---------------------------------------------------------------------------
//
// The list endpoint accepts a small set of filter knobs; we serialize
// them into the query key as-is so two pages with different filters
// don't collide. Detail keeps its own per-id slice so the live SSE
// stream can patch a single thread without touching unrelated rows.

export function useConversations(
  filters?: ConversationListFilters,
  opts?: SliceOptions<ConversationSummary[]>,
): UseQueryResult<ConversationSummary[], Error> {
  return useQuery({
    queryKey: queryKeys.conversations.list(
      filters as Record<string, unknown> | undefined,
    ),
    queryFn: () => api.listConversations(filters),
    ...opts,
  });
}

export function useConversation(
  id: string,
  opts?: SliceOptions<ConversationDetail | null>,
): UseQueryResult<ConversationDetail | null, Error> {
  return useQuery({
    queryKey: queryKeys.conversations.detail(id),
    // Surface 404 as `null` so the detail page can render a clean "not
    // found" state instead of bubbling an ApiError up to the boundary.
    queryFn: async () => {
      try {
        return await api.getConversation(id);
      } catch (err) {
        if (
          err instanceof Error &&
          /API error 404/.test(err.message)
        ) {
          return null;
        }
        throw err;
      }
    },
    enabled: opts?.enabled ?? Boolean(id),
    refetchInterval: opts?.refetchInterval,
    staleTime: opts?.staleTime,
  });
}

export function useInbox(
  opts?: SliceOptions<InboxItem[]>,
): UseQueryResult<InboxItem[], Error> {
  return useQuery({
    queryKey: queryKeys.conversations.inbox(),
    queryFn: () => api.listInbox(),
    ...opts,
  });
}


// ---------------------------------------------------------------------------
// Tenant
// ---------------------------------------------------------------------------

export function useTenantBudget(
  opts?: SliceOptions<BudgetResponse | null>,
): UseQueryResult<BudgetResponse | null, Error> {
  return useQuery({
    queryKey: queryKeys.tenant.budget(),
    queryFn: async () => {
      try {
        return await api.getTenantBudget();
      } catch {
        // No tenant budget set yet — surface null instead of an error.
        return null;
      }
    },
    ...opts,
  });
}

// ---------------------------------------------------------------------------
// Templates & connectors (create-unit wizard)
// ---------------------------------------------------------------------------

export function useUnitTemplates(
  opts?: SliceOptions<UnitTemplateSummary[]>,
): UseQueryResult<UnitTemplateSummary[], Error> {
  return useQuery({
    queryKey: queryKeys.templates.list(),
    queryFn: () => api.listUnitTemplates(),
    ...opts,
  });
}

// ---------------------------------------------------------------------------
// Packages (#395 / PR-PLAT-PKG-1). `/packages` is a sidebar entry
// in the portal IA (§ 3.2) and the data the CLI's `spring package
// list / show` consumes too — both surfaces ride these hooks.
// ---------------------------------------------------------------------------

export function usePackages(
  opts?: SliceOptions<PackageSummary[]>,
): UseQueryResult<PackageSummary[], Error> {
  return useQuery({
    queryKey: queryKeys.packages.list(),
    queryFn: () => api.listPackages(),
    ...opts,
  });
}

export function usePackage(
  name: string,
  opts?: SliceOptions<PackageDetail | null>,
): UseQueryResult<PackageDetail | null, Error> {
  return useQuery({
    queryKey: queryKeys.packages.detail(name),
    queryFn: () => api.getPackage(name),
    enabled: opts?.enabled ?? Boolean(name),
    refetchInterval: opts?.refetchInterval,
    staleTime: opts?.staleTime,
  });
}

export function useUnitTemplateDetail(
  pkg: string,
  name: string,
  opts?: SliceOptions<UnitTemplateDetail | null>,
): UseQueryResult<UnitTemplateDetail | null, Error> {
  return useQuery({
    queryKey: queryKeys.templates.detail(pkg, name),
    queryFn: () => api.getUnitTemplate(pkg, name),
    enabled: opts?.enabled ?? Boolean(pkg && name),
    refetchInterval: opts?.refetchInterval,
    staleTime: opts?.staleTime,
  });
}

export function useConnectorTypes(
  opts?: SliceOptions<ConnectorTypeResponse[]>,
): UseQueryResult<ConnectorTypeResponse[], Error> {
  return useQuery({
    queryKey: queryKeys.connectors.list(),
    queryFn: () => api.listConnectors(),
    ...opts,
  });
}

/**
 * Single connector type by slug or id. Returns `null` when the
 * connector isn't registered (404), so the detail page can render
 * a clean not-found state without trapping the error boundary.
 */
export function useConnector(
  slugOrId: string,
  opts?: SliceOptions<ConnectorTypeResponse | null>,
): UseQueryResult<ConnectorTypeResponse | null, Error> {
  return useQuery({
    queryKey: queryKeys.connectors.detail(slugOrId),
    queryFn: () => api.getConnector(slugOrId),
    enabled: opts?.enabled ?? Boolean(slugOrId),
    refetchInterval: opts?.refetchInterval,
    staleTime: opts?.staleTime,
  });
}

/**
 * JSON Schema describing the connector's per-unit config body. Each
 * connector ships its own schema at `/api/v1/connectors/{slug}/config-schema`;
 * the result is rendered as-is (pretty-printed JSON) on the detail page.
 * Surfaces `null` for connectors that don't ship a schema endpoint so
 * the page can show "(not advertised)" instead of erroring out.
 */
export function useConnectorConfigSchema(
  slug: string,
  opts?: SliceOptions<unknown | null>,
): UseQueryResult<unknown | null, Error> {
  return useQuery({
    queryKey: [...queryKeys.connectors.detail(slug), "schema"] as const,
    queryFn: async () => {
      try {
        return await api.getConnectorConfigSchema(slug);
      } catch {
        return null;
      }
    },
    enabled: opts?.enabled ?? Boolean(slug),
    refetchInterval: opts?.refetchInterval,
    staleTime: opts?.staleTime,
  });
}

/**
 * Row shape the connector detail page and command palette consume to
 * render "units bound to this connector". Pre-#520 this was stitched
 * together on the client by fanning out `api.getUnitConnector(u.id)`
 * across every unit returned by `api.listUnits()`; the walk bypassed
 * TanStack's per-query cache and serialised behind the browser's
 * parallel-connection cap for tenants with many units. The row shape
 * is preserved verbatim so call sites don't change.
 */
export interface UnitConnectorBindingRow {
  unitId: string;
  unitName: string;
  unitDisplayName: string;
  typeId: string | null;
  typeSlug: string | null;
}

/**
 * Lists every unit bound to the given connector type in a single
 * round-trip (#520). Rides the new bulk endpoint
 * `GET /api/v1/connectors/{slugOrId}/bindings`, so the portal's N+1
 * walk from #516 is gone and the CLI's `spring connector bindings
 * <slug>` rides the same data source. Boundary enforcement is the
 * server's job: the endpoint walks the same directory surface the
 * canonical `/api/v1/units` list does, so whatever visibility filter
 * wraps unit listing applies transparently here too.
 */
export function useConnectorBindings(
  slugOrId: string,
  opts?: SliceOptions<UnitConnectorBindingRow[]>,
): UseQueryResult<UnitConnectorBindingRow[], Error> {
  return useQuery({
    queryKey: [...queryKeys.connectors.detail(slugOrId), "bindings"] as const,
    queryFn: async (): Promise<UnitConnectorBindingRow[]> => {
      const rows = await api.listConnectorBindings(slugOrId);
      return rows.map((b) => ({
        unitId: b.unitId,
        unitName: b.unitName,
        unitDisplayName: b.unitDisplayName,
        typeId: b.typeId ?? null,
        typeSlug: b.typeSlug ?? null,
      }));
    },
    enabled: opts?.enabled ?? Boolean(slugOrId),
    refetchInterval: opts?.refetchInterval,
    staleTime: opts?.staleTime,
  });
}

// ---------------------------------------------------------------------------
// Lightweight list queries (used by the expertise directory surface, which
// fans out reads over every agent/unit the tenant can see).
// ---------------------------------------------------------------------------

export function useAgents(
  opts?: SliceOptions<AgentResponse[]>,
): UseQueryResult<AgentResponse[], Error> {
  return useQuery({
    queryKey: queryKeys.agents.list(),
    queryFn: () => api.listAgents(),
    ...opts,
  });
}

export function useUnits(
  opts?: SliceOptions<UnitResponse[]>,
): UseQueryResult<UnitResponse[], Error> {
  return useQuery({
    queryKey: queryKeys.units.list(),
    queryFn: () => api.listUnits(),
    ...opts,
  });
}

// ---------------------------------------------------------------------------
// Expertise directory (#412 / #486)
// ---------------------------------------------------------------------------
//
// Read hooks only; writes flow through `api.setAgentExpertise` /
// `api.setUnitOwnExpertise` and hand-seed + invalidate the cache from
// the component so the aggregated view on every ancestor also refetches.

export function useAgentExpertise(
  id: string,
  opts?: SliceOptions<ExpertiseDomainDto[]>,
): UseQueryResult<ExpertiseDomainDto[], Error> {
  return useQuery({
    queryKey: queryKeys.agents.expertise(id),
    queryFn: () => api.getAgentExpertise(id),
    enabled: opts?.enabled ?? Boolean(id),
    refetchInterval: opts?.refetchInterval,
    staleTime: opts?.staleTime,
  });
}

export function useUnitOwnExpertise(
  id: string,
  opts?: SliceOptions<ExpertiseDomainDto[]>,
): UseQueryResult<ExpertiseDomainDto[], Error> {
  return useQuery({
    queryKey: queryKeys.units.ownExpertise(id),
    queryFn: () => api.getUnitOwnExpertise(id),
    enabled: opts?.enabled ?? Boolean(id),
    refetchInterval: opts?.refetchInterval,
    staleTime: opts?.staleTime,
  });
}

export function useUnitAggregatedExpertise(
  id: string,
  opts?: SliceOptions<AggregatedExpertiseResponse>,
): UseQueryResult<AggregatedExpertiseResponse, Error> {
  return useQuery({
    queryKey: queryKeys.units.aggregatedExpertise(id),
    queryFn: () => api.getUnitAggregatedExpertise(id),
    enabled: opts?.enabled ?? Boolean(id),
    refetchInterval: opts?.refetchInterval,
    staleTime: opts?.staleTime,
  });
}

// ---------------------------------------------------------------------------
// Ollama (model discovery for dapr-agent + ollama hosting)
// ---------------------------------------------------------------------------

export interface OllamaModelEntry {
  name: string;
  size: number;
  modifiedAt: string | null;
}

// ---------------------------------------------------------------------------
// Settings drawer (#451) — platform metadata + auth view
// ---------------------------------------------------------------------------
//
// The About and Auth panels on the Settings drawer read small,
// low-churn slices. `staleTime: Infinity` for platform info (the value
// can't change without a redeploy); auth/me similarly stable within a
// session; tokens refreshes on focus so newly-minted tokens surface
// without a page reload once token CRUD ships (#557).

export function usePlatformInfo(
  opts?: SliceOptions<PlatformInfoResponse | null>,
): UseQueryResult<PlatformInfoResponse | null, Error> {
  return useQuery({
    queryKey: queryKeys.platform.info(),
    queryFn: async () => {
      try {
        return await api.getPlatformInfo();
      } catch {
        // The About panel surfaces "(unavailable)" when the platform
        // endpoint is unreachable (older servers, network blip) rather
        // than bubbling an error up to the drawer boundary.
        return null;
      }
    },
    ...opts,
  });
}

export function useCurrentUser(
  opts?: SliceOptions<UserProfileResponse | null>,
): UseQueryResult<UserProfileResponse | null, Error> {
  return useQuery({
    queryKey: queryKeys.auth.me(),
    queryFn: async () => {
      try {
        return await api.getCurrentUser();
      } catch {
        // Anonymous / unauthenticated — surface null so the Auth panel
        // can render the "not signed in" state.
        return null;
      }
    },
    ...opts,
  });
}

export function useAuthTokens(
  opts?: SliceOptions<TokenResponse[]>,
): UseQueryResult<TokenResponse[], Error> {
  return useQuery({
    queryKey: queryKeys.auth.tokens(),
    queryFn: async () => {
      try {
        return await api.listAuthTokens();
      } catch {
        // Tokens live behind auth; return an empty list when the caller
        // is anonymous so the panel can render the empty state.
        return [];
      }
    },
    ...opts,
  });
}

export function useOllamaModels(
  opts?: SliceOptions<OllamaModelEntry[] | null>,
): UseQueryResult<OllamaModelEntry[] | null, Error> {
  return useQuery({
    queryKey: queryKeys.ollama.models(),
    // A missing Ollama server is expected during regular development —
    // surface null so the wizard can fall back to the static catalog
    // without blowing up the page.
    queryFn: async () => {
      try {
        return await api.listOllamaModels();
      } catch {
        return null;
      }
    },
    ...opts,
  });
}

/**
 * Dynamic model-list hook for the unit-creation wizard (#597). Queries
 * <c>GET /api/v1/models/{provider}</c> which either returns a list fetched
 * live from the provider's API or the server-side static fallback — either
 * way the endpoint succeeds with a list, so this hook never throws into
 * the page. Callers still guard with `enabled` to avoid a network round-
 * trip when they know they want the hard-coded list (e.g. YAML import).
 */
export function useProviderModels(
  provider: string,
  opts?: SliceOptions<string[] | null>,
): UseQueryResult<string[] | null, Error> {
  return useQuery({
    queryKey: queryKeys.models.forProvider(provider),
    queryFn: async () => {
      try {
        return await api.listProviderModels(provider);
      } catch {
        // The server already falls back to the static list on provider
        // errors; a null here means the call itself failed (auth, CORS,
        // etc.). Surface null so the wizard can still render its static
        // catalog from `ai-models.ts`.
        return null;
      }
    },
    // Models don't change minute-by-minute. Match the server's cache TTL
    // so repeated wizard opens don't re-issue the request.
    staleTime: opts?.staleTime ?? 60 * 60 * 1000,
    refetchInterval: opts?.refetchInterval,
    enabled: opts?.enabled ?? Boolean(provider),
  });
}

/**
 * Provider credential-status hook (#598). Asks the server whether the
 * currently-selected LLM provider's credentials are configured (or, for
 * Ollama, whether the configured endpoint is reachable). Drives the
 * inline banner in the unit-creation wizard's Step 1 so operators don't
 * discover "not configured" at dispatch time.
 *
 * The payload never carries key material — the endpoint returns only
 * booleans, the source tier ("unit" | "tenant" | null), and an
 * operator-facing suggestion string. The plaintext stays server-side.
 *
 * Stale time is 30 seconds — long enough that cycling through the
 * provider dropdown while typing a key doesn't hammer the endpoint, but
 * short enough that a freshly-set tenant default appears on the next
 * render after the Settings drawer closes.
 */
export function useProviderCredentialStatus(
  provider: string,
  opts?: SliceOptions<import("./types").ProviderCredentialStatusResponse | null>,
): UseQueryResult<import("./types").ProviderCredentialStatusResponse | null, Error> {
  return useQuery({
    queryKey: ["system", "credentials", provider] as const,
    queryFn: async () => {
      try {
        return await api.getProviderCredentialStatus(provider);
      } catch {
        // Anonymous / offline / server-error — surface null so the banner
        // can render a muted "could not verify" fallback. The query
        // client still throws loudly in devtools for debugging.
        return null;
      }
    },
    staleTime: opts?.staleTime ?? 30 * 1000,
    refetchInterval: opts?.refetchInterval,
    enabled: opts?.enabled ?? Boolean(provider),
  });
}
