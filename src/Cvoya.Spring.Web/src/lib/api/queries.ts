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
  useMutation,
  useQuery,
  useQueryClient,
  type UseMutationResult,
  type UseQueryOptions,
  type UseQueryResult,
} from "@tanstack/react-query";

import { api } from "./client";
import { queryKeys } from "./query-keys";
import {
  validateTenantTreeResponse,
  type ValidatedTenantTreeNode,
} from "./validate-tenant-tree";
import type {
  ActivityQueryResult,
  AgentDashboardSummary,
  AgentDetailResponse,
  AgentExecutionResponse,
  AgentResponse,
  AgentSkillsResponse,
  AggregatedExpertiseResponse,
  BudgetResponse,
  CloneResponse,
  InstalledConnectorResponse,
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
  MemoriesResponse,
  PackageDetail,
  PackageSummary,
  PersistentAgentDeploymentResponse,
  PersistentAgentLogsResponse,
  PlatformInfoResponse,
  SkillCatalogEntry,
  ThroughputRollupResponse,
  TokenResponse,
  UnitBoundaryResponse,
  UnitDashboardSummary,
  UnitDetailResponse,
  UnitExecutionResponse,
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

/**
 * Read a unit's persisted execution defaults (#601 / #603 / #409
 * B-wide, backend PR #628). The endpoint always returns the empty shape
 * (every field null) when the unit has never had an execution block
 * persisted, so callers never branch on 404 vs unset. The Execution tab
 * on `/units/[id]` and the agent-side "inherited from unit" indicator
 * both ride this hook.
 */
export function useUnitExecution(
  id: string,
  opts?: SliceOptions<UnitExecutionResponse>,
): UseQueryResult<UnitExecutionResponse, Error> {
  return useQuery({
    queryKey: queryKeys.units.execution(id),
    queryFn: () => api.getUnitExecution(id),
    enabled: opts?.enabled ?? Boolean(id),
    refetchInterval: opts?.refetchInterval,
    staleTime: opts?.staleTime,
  });
}

/**
 * Read an agent's own declared execution block (#601 / #603 / #409
 * B-wide, backend PR #628). The response carries only the agent's
 * declared fields — inherited unit defaults are merged at dispatch
 * time. The Execution panel on `/agents/[id]` overlays the owning
 * unit's execution (via {@link useUnitExecution}) to render the
 * "inherited from unit" indicator for fields the agent leaves blank.
 */
export function useAgentExecution(
  id: string,
  opts?: SliceOptions<AgentExecutionResponse>,
): UseQueryResult<AgentExecutionResponse, Error> {
  return useQuery({
    queryKey: queryKeys.agents.execution(id),
    queryFn: () => api.getAgentExecution(id),
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

/**
 * Read an agent's currently-equipped skills (QUALITY-agent-skills-write,
 * #900). The Explorer's Agent → Skills tab rides this hook; it mirrors
 * `spring agent skills get` on the wire.
 */
export function useAgentSkills(
  id: string,
  opts?: SliceOptions<AgentSkillsResponse>,
): UseQueryResult<AgentSkillsResponse, Error> {
  return useQuery({
    queryKey: queryKeys.agents.skills(id),
    queryFn: () => api.getAgentSkills(id),
    enabled: opts?.enabled ?? Boolean(id),
    refetchInterval: opts?.refetchInterval,
    staleTime: opts?.staleTime,
  });
}

/**
 * Tenant skill catalog (QUALITY-agent-skills-write, #900). Feeds the
 * "Add skill" combobox on the Explorer Agent → Skills tab; matches
 * `spring skills list` on the wire. Catalog is low-churn (changes only
 * when a connector is installed or a registry updates) so the default
 * staleness is long enough to dedupe repeat opens of the tab without
 * trapping freshly-installed entries.
 */
export function useSkillsCatalog(
  opts?: SliceOptions<SkillCatalogEntry[]>,
): UseQueryResult<SkillCatalogEntry[], Error> {
  return useQuery({
    queryKey: queryKeys.skills.catalog(),
    queryFn: () => api.listSkills(),
    staleTime: opts?.staleTime ?? 5 * 60 * 1000,
    refetchInterval: opts?.refetchInterval,
    enabled: opts?.enabled ?? true,
  });
}

/**
 * Replace the agent's skill set (QUALITY-agent-skills-write, #900). The
 * server PUT is a full replacement, so callers pass the complete
 * post-mutation list (not a diff). Invalidates
 * `queryKeys.agents.skills(id)` on success so the tab list refreshes.
 *
 * Mirrors `spring agent skills set <agent> -- <skill>…` on the CLI.
 */
export function useSetAgentSkills(
  id: string,
): UseMutationResult<AgentSkillsResponse, Error, string[]> {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: (skills: string[]) => api.setAgentSkills(id, skills),
    onSuccess: (data) => {
      // Seed the cache with the server's authoritative list (PUT is a
      // full replacement), then invalidate so any other observer
      // refetches if needed.
      queryClient.setQueryData(queryKeys.agents.skills(id), data);
      queryClient.invalidateQueries({ queryKey: queryKeys.agents.skills(id) });
    },
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

/**
 * Lists every connector installed on the current tenant (#714). Post-#714
 * this is the only connector list on the wire — the generic
 * "every connector the host knows about" endpoint was retired in favour
 * of tenant-install semantics. A connector package registered with the
 * host but not installed on the tenant is invisible here and in the
 * wizard's chooser.
 */
export function useConnectorTypes(
  opts?: SliceOptions<InstalledConnectorResponse[]>,
): UseQueryResult<InstalledConnectorResponse[], Error> {
  return useQuery({
    queryKey: queryKeys.connectors.list(),
    queryFn: () => api.listConnectors(),
    ...opts,
  });
}

/**
 * Single installed connector by slug or id. Returns `null` when the
 * connector isn't installed on the current tenant (404 post-#714, which
 * collapses the "not registered" and "not installed" cases into one
 * not-found state for the detail page). Pre-#714 this returned the
 * registry descriptor regardless of install state.
 */
export function useConnector(
  slugOrId: string,
  opts?: SliceOptions<InstalledConnectorResponse | null>,
): UseQueryResult<InstalledConnectorResponse | null, Error> {
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
 * Tenant-installed agent runtimes (#690). The wizard reads every
 * available runtime from this hook — the list drives the provider/tool
 * dropdown and each entry's `credentialKind` + `credentialDisplayHint`
 * drive the credential input.
 */
export function useAgentRuntimes(
  opts?: SliceOptions<
    import("./types").InstalledAgentRuntimeResponse[]
  >,
): UseQueryResult<
  import("./types").InstalledAgentRuntimeResponse[],
  Error
> {
  return useQuery({
    queryKey: queryKeys.agentRuntimes.list(),
    queryFn: () => api.listAgentRuntimes(),
    staleTime: opts?.staleTime ?? 60 * 60 * 1000,
    refetchInterval: opts?.refetchInterval,
    enabled: opts?.enabled ?? true,
  });
}

/**
 * Per-runtime model catalog (#690). Returns the tenant's configured
 * model list for a runtime; feeds the wizard's Model dropdown when a
 * runtime is selected.
 */
export function useAgentRuntimeModels(
  runtimeId: string,
  opts?: SliceOptions<import("./types").AgentRuntimeModelResponse[]>,
): UseQueryResult<import("./types").AgentRuntimeModelResponse[], Error> {
  return useQuery({
    queryKey: queryKeys.agentRuntimes.models(runtimeId),
    queryFn: () => api.getAgentRuntimeModels(runtimeId),
    staleTime: opts?.staleTime ?? 60 * 60 * 1000,
    refetchInterval: opts?.refetchInterval,
    enabled: opts?.enabled ?? Boolean(runtimeId),
  });
}

/**
 * Persistent credential-health row for an agent runtime (#691). Feeds
 * the read-only admin view at `/admin/agent-runtimes`. Returns `null`
 * when the endpoint 404s — a fresh install that has never been probed
 * has no row yet, so the portal renders a muted "No signal yet" state
 * rather than trapping the admin error boundary.
 */
export function useAgentRuntimeCredentialHealth(
  runtimeId: string,
  secretName?: string,
  opts?: SliceOptions<import("./types").CredentialHealthResponse | null>,
): UseQueryResult<import("./types").CredentialHealthResponse | null, Error> {
  return useQuery({
    queryKey: queryKeys.agentRuntimes.credentialHealth(runtimeId, secretName),
    queryFn: async () => {
      try {
        return await api.getAgentRuntimeCredentialHealth(runtimeId, secretName);
      } catch {
        return null;
      }
    },
    staleTime: opts?.staleTime ?? 30 * 1000,
    refetchInterval: opts?.refetchInterval,
    enabled: opts?.enabled ?? Boolean(runtimeId),
  });
}

/**
 * Persistent credential-health row for a connector (#691). Feeds the
 * read-only admin view at `/admin/connectors`. Mirrors
 * {@link useAgentRuntimeCredentialHealth}; same null-on-404 semantics.
 */
export function useConnectorCredentialHealth(
  slugOrId: string,
  secretName?: string,
  opts?: SliceOptions<import("./types").CredentialHealthResponse | null>,
): UseQueryResult<import("./types").CredentialHealthResponse | null, Error> {
  return useQuery({
    queryKey: queryKeys.connectors.credentialHealth(slugOrId, secretName),
    queryFn: async () => {
      try {
        return await api.getConnectorCredentialHealth(slugOrId, secretName);
      } catch {
        return null;
      }
    },
    staleTime: opts?.staleTime ?? 30 * 1000,
    refetchInterval: opts?.refetchInterval,
    enabled: opts?.enabled ?? Boolean(slugOrId),
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

// ---------------------------------------------------------------------------
// Tenant tree (SVR-tenant-tree, umbrella #815).
// ---------------------------------------------------------------------------

/**
 * Single-payload tenant snapshot consumed by `<UnitExplorer>`.
 *
 * The raw response runs through `validateTenantTreeResponse` before
 * reaching callers, so stray `kind`/`status` values from the server
 * coerce to safe defaults and log via `console.error`
 * (see `FOUND-tree-boundary-validate`). The Explorer always renders a
 * well-formed tree.
 *
 * The endpoint caps at ≤500 nodes per tenant in v2.0 — see plan §3 of
 * the umbrella. Larger tenants degrade gracefully; lazy expansion is
 * tracked separately as `V21-tenant-tree-lazy`.
 */
export function useTenantTree(
  opts?: SliceOptions<ValidatedTenantTreeNode>,
): UseQueryResult<ValidatedTenantTreeNode, Error> {
  return useQuery({
    queryKey: queryKeys.tenant.tree(),
    queryFn: async () => validateTenantTreeResponse(await api.getTenantTree()),
    ...opts,
  });
}

// ---------------------------------------------------------------------------
// Memories inspector (SVR-memories, umbrella #815).
// ---------------------------------------------------------------------------

/**
 * Read the short-term + long-term memory entries for a unit or agent.
 *
 * In v2.0 both lists always come back empty — the real backing store
 * ships in `V21-memory-write`. The hook exists now so the Explorer's
 * Memory tab can wire the empty-state render during the foundation
 * rollout.
 */
export function useMemories(
  scope: "unit" | "agent",
  id: string,
  opts?: SliceOptions<MemoriesResponse>,
): UseQueryResult<MemoriesResponse, Error> {
  return useQuery({
    queryKey:
      scope === "unit"
        ? queryKeys.memories.unit(id)
        : queryKeys.memories.agent(id),
    queryFn: () =>
      scope === "unit" ? api.getUnitMemories(id) : api.getAgentMemories(id),
    enabled: opts?.enabled ?? Boolean(id),
    staleTime: opts?.staleTime,
    refetchInterval: opts?.refetchInterval,
  });
}
