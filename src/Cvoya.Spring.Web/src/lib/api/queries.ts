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
  BudgetResponse,
  CloneResponse,
  ConnectorTypeResponse,
  ConversationDetail,
  ConversationListFilters,
  ConversationSummary,
  CostDashboardSummary,
  CostSummaryResponse,
  DashboardSummary,
  InboxItem,
  InitiativeLevelResponse,
  InitiativePolicy,
  PackageDetail,
  PackageSummary,
  PersistentAgentDeploymentResponse,
  PersistentAgentLogsResponse,
  UnitBoundaryResponse,
  UnitDashboardSummary,
  UnitDetailResponse,
  UnitPolicyResponse,
  UnitReadinessResponse,
  UnitResponse,
  UnitTemplateDetail,
  UnitTemplateSummary,
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
 * Fan-out query that pairs every visible unit with its active
 * connector binding pointer (or `null` when unbound) so the detail
 * page can list "units bound to this connector".
 *
 * The unit list endpoint doesn't carry connector pointers, so we have
 * to N+1 it. The route parameter is `slugOrId`, so we accept either
 * form and match each binding on whichever identifier the pointer
 * carries. Follow-up #520 tracks adding a bulk "units bound to type X"
 * endpoint so this walk can go away.
 */
export interface UnitConnectorBindingRow {
  unitId: string;
  unitName: string;
  unitDisplayName: string;
  typeId: string | null;
  typeSlug: string | null;
}

export function useConnectorBindings(
  slugOrId: string,
  opts?: SliceOptions<UnitConnectorBindingRow[]>,
): UseQueryResult<UnitConnectorBindingRow[], Error> {
  return useQuery({
    queryKey: [...queryKeys.connectors.detail(slugOrId), "bindings"] as const,
    queryFn: async (): Promise<UnitConnectorBindingRow[]> => {
      const units = await api.listUnits();
      const needle = slugOrId.toLowerCase();
      const rows: (UnitConnectorBindingRow | null)[] = await Promise.all(
        units.map(async (u): Promise<UnitConnectorBindingRow | null> => {
          const ptr = await api.getUnitConnector(u.id);
          if (!ptr) return null;
          const matches =
            (ptr.typeSlug && ptr.typeSlug.toLowerCase() === needle) ||
            (ptr.typeId && ptr.typeId.toLowerCase() === needle);
          if (!matches) return null;
          return {
            unitId: u.id,
            unitName: u.name,
            unitDisplayName: u.displayName,
            typeId: ptr.typeId ?? null,
            typeSlug: ptr.typeSlug ?? null,
          };
        }),
      );
      return rows.filter((r): r is UnitConnectorBindingRow => r !== null);
    },
    enabled: opts?.enabled ?? Boolean(slugOrId),
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
