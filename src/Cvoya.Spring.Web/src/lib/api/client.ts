import createClient from "openapi-fetch";

import type { paths } from "./schema";
import type {
  AgentCloningPolicyResponse,
  AgentDetailResponse,
  AgentExecutionResponse,
  AgentResponse,
  ThreadListFilters,
  ThreadMessageRequest,
  CreateAgentRequest,
  CreateCloneRequest,
  CreateSecretRequest,
  CreateTokenRequest,
  CreateTokenResponse,
  CreateUnitFromTemplateRequest,
  CreateUnitFromYamlRequest,
  DashboardSummary,
  DeployPersistentAgentRequest,
  DirectorySearchRequest,
  DirectorySearchResponse,
  ExpertiseDomainDto,
  InitiativePolicy,
  MessageResponse,
  PersistentAgentDeploymentResponse,
  PersistentAgentLogsResponse,
  ScalePersistentAgentRequest,
  SendMessageRequest,
  SetBudgetRequest,
  UnitBoundaryResponse,
  UnitConnectorBindingRequest,
  UnitExecutionResponse,
  UnitGitHubConfigRequest,
  UnitOrchestrationResponse,
  UnitPolicyResponse,
  UnitResponse,
  UpdateAgentMetadataRequest,
} from "./types";

const BASE = process.env.NEXT_PUBLIC_API_URL ?? "";

const fetchClient = createClient<paths>({ baseUrl: BASE });

/**
 * Thrown by every `api.*` method on a non-2xx response. Keeps the
 * previous hand-rolled error shape (`message` formatted as
 * `API error {status}: {statusText} — {body}`) so call sites that
 * inspect `err.message` don't change.
 */
export class ApiError extends Error {
  constructor(
    public readonly status: number,
    public readonly statusText: string,
    public readonly body: unknown,
  ) {
    const detail =
      typeof body === "string"
        ? body
        : body
          ? JSON.stringify(body)
          : "";
    super(
      `API error ${status}: ${statusText}${detail ? ` — ${detail}` : ""}`,
    );
    this.name = "ApiError";
  }
}

type FetchResult<T> = {
  data?: T;
  error?: unknown;
  response: Response;
};

/**
 * Unwrap an `openapi-fetch` result that is expected to return `T`.
 * Throws `ApiError` on non-2xx; throws a plain `Error` if the server
 * returned 2xx with an empty body when a payload was expected (should
 * not happen in practice but keeps the TypeScript return type honest).
 */
function unwrap<T>(result: FetchResult<T>): T {
  if (result.error !== undefined || !result.response.ok) {
    throw new ApiError(
      result.response.status,
      result.response.statusText,
      result.error ?? null,
    );
  }
  if (result.data === undefined) {
    throw new Error(
      `API ${result.response.status}: response body was empty (expected payload)`,
    );
  }
  return result.data;
}

/**
 * Unwrap an `openapi-fetch` result whose endpoint legitimately returns
 * no body (204 NoContent, 202 Accepted without content). Throws only
 * when the status is non-2xx.
 */
function assertOk(result: FetchResult<unknown>): void {
  if (result.error !== undefined || !result.response.ok) {
    throw new ApiError(
      result.response.status,
      result.response.statusText,
      result.error ?? null,
    );
  }
}

/**
 * Stamp `isTopLevel: true` on a unit-creation request body unless the
 * caller already supplied a parent form (either `parentUnitIds` with
 * entries or an explicit `isTopLevel`). Review feedback on #744 made
 * the parent form mandatory at the server; the wizard currently only
 * produces top-level units, so the client defaults the flag so the
 * existing wizard path keeps working while the parent-unit picker UI
 * lands later.
 */
function withDefaultParentParent<T>(body: T): T {
  const shaped = body as {
    parentUnitIds?: string[] | null;
    isTopLevel?: boolean | null;
  };
  const hasParents =
    Array.isArray(shaped.parentUnitIds) && shaped.parentUnitIds.length > 0;
  if (hasParents || shaped.isTopLevel !== undefined && shaped.isTopLevel !== null) {
    return body;
  }
  return { ...body, isTopLevel: true } as T;
}

export const api = {
  // Dashboard
  getDashboardSummary: async (): Promise<DashboardSummary> => {
    const resp = await fetch(`${BASE}/api/v1/tenant/dashboard/summary`);
    if (!resp.ok) {
      throw new ApiError(resp.status, resp.statusText, await resp.text());
    }
    return resp.json() as Promise<DashboardSummary>;
  },
  getDashboardAgents: async () =>
    unwrap(await fetchClient.GET("/api/v1/tenant/dashboard/agents")),
  getDashboardUnits: async () =>
    unwrap(await fetchClient.GET("/api/v1/tenant/dashboard/units")),
  getDashboardCosts: async () =>
    unwrap(await fetchClient.GET("/api/v1/tenant/dashboard/costs")),

  // Agents
  listAgents: async () => unwrap(await fetchClient.GET("/api/v1/tenant/agents")),
  /**
   * Create a new agent. Mirrors the CLI's `spring agent create` 1:1 —
   * the server requires at least one unit assignment (#744) and accepts
   * an optional `definitionJson` blob carrying the execution config
   * (tool / runtime / image / model). Surfaced through the portal's
   * `/agents/create` page and the inline-create dialog reachable from
   * a unit's Agents tab; both flows funnel through the shared helper
   * in `@/lib/agents/create-agent`.
   */
  createAgent: async (body: CreateAgentRequest): Promise<AgentResponse> =>
    unwrap(
      await fetchClient.POST("/api/v1/tenant/agents", { body }),
    ) as AgentResponse,
  // The generated type for GET /api/v1/agents/{id} is AgentDetailResponse;
  // the handler falls back to returning `{ agent, status: null }` when the
  // StatusQuery to the actor fails. Existing call sites expect
  // AgentDetailResponse, so surface that directly.
  getAgent: async (id: string): Promise<AgentDetailResponse> =>
    unwrap(
      await fetchClient.GET("/api/v1/tenant/agents/{id}", {
        params: { path: { id } },
      }),
    ) as AgentDetailResponse,
  updateAgentMetadata: async (id: string, patch: UpdateAgentMetadataRequest) =>
    unwrap(
      await fetchClient.PATCH("/api/v1/tenant/agents/{id}", {
        params: { path: { id } },
        body: patch,
      }),
    ),
  getAgentSkills: async (id: string) =>
    unwrap(
      await fetchClient.GET("/api/v1/tenant/agents/{id}/skills", {
        params: { path: { id } },
      }),
    ),
  setAgentSkills: async (id: string, skills: string[]) =>
    unwrap(
      await fetchClient.PUT("/api/v1/tenant/agents/{id}/skills", {
        params: { path: { id } },
        body: { skills },
      }),
    ),
  deleteAgent: async (id: string): Promise<void> => {
    assertOk(
      await fetchClient.DELETE("/api/v1/tenant/agents/{id}", {
        params: { path: { id } },
      }),
    );
  },

  // Persistent-agent lifecycle (#396 / PR-PLAT-RUN-2b). The same verbs the
  // CLI ships under `spring agent {deploy,undeploy,scale,logs}` plus the
  // read-only `deployment` inspector. Ephemeral agents will receive a 400
  // from the server; the portal surfaces that verbatim per the CLI-parity
  // rule in AGENTS.md.
  deployPersistentAgent: async (
    id: string,
    body?: DeployPersistentAgentRequest,
  ): Promise<PersistentAgentDeploymentResponse> =>
    unwrap(
      await fetchClient.POST("/api/v1/tenant/agents/{id}/deploy", {
        params: { path: { id } },
        // The server's request body is optional (oneOf null). Omit the
        // body entirely when no overrides are supplied so the openapi
        // client doesn't serialize an empty object.
        ...(body !== undefined ? { body } : {}),
      }),
    ) as PersistentAgentDeploymentResponse,
  undeployPersistentAgent: async (
    id: string,
  ): Promise<PersistentAgentDeploymentResponse> =>
    unwrap(
      await fetchClient.POST("/api/v1/tenant/agents/{id}/undeploy", {
        params: { path: { id } },
      }),
    ) as PersistentAgentDeploymentResponse,
  scalePersistentAgent: async (
    id: string,
    body: ScalePersistentAgentRequest,
  ): Promise<PersistentAgentDeploymentResponse> =>
    unwrap(
      await fetchClient.POST("/api/v1/tenant/agents/{id}/scale", {
        params: { path: { id } },
        body,
      }),
    ) as PersistentAgentDeploymentResponse,
  getPersistentAgentLogs: async (
    id: string,
    tail?: number,
  ): Promise<PersistentAgentLogsResponse> => {
    const query = tail != null ? { tail } : undefined;
    return unwrap(
      await fetchClient.GET("/api/v1/tenant/agents/{id}/logs", {
        params: {
          path: { id },
          ...(query ? { query: query as never } : {}),
        },
      }),
    ) as PersistentAgentLogsResponse;
  },
  getPersistentAgentDeployment: async (
    id: string,
  ): Promise<PersistentAgentDeploymentResponse> =>
    unwrap(
      await fetchClient.GET("/api/v1/tenant/agents/{id}/deployment", {
        params: { path: { id } },
      }),
    ) as PersistentAgentDeploymentResponse,

  // Units
  //
  // Lightweight list of every unit the caller can see. Used by the
  // sub-units picker (#352) to offer candidates when adding a child
  // unit to a parent.
  listUnits: async () => unwrap(await fetchClient.GET("/api/v1/tenant/units")),
  // Detailed unit read — includes Members and raw status payload. Used by
  // the legacy query-string detail view under /units?id=... and still
  // useful for anything that needs the members/details blob.
  getUnitDetail: async (id: string) =>
    unwrap(
      await fetchClient.GET("/api/v1/tenant/units/{id}", {
        params: { path: { id } },
      }),
    ),
  // Lightweight unit read that returns the unit envelope only. Used by
  // the /units/[id] config page where the tabs shell pulls data
  // independently.
  getUnit: async (id: string): Promise<UnitResponse> => {
    const detail = unwrap(
      await fetchClient.GET("/api/v1/tenant/units/{id}", {
        params: { path: { id } },
      }),
    );
    return detail.unit as UnitResponse;
  },
  createUnit: async (body: {
    name: string;
    displayName: string;
    description: string;
    model?: string;
    color?: string;
    // #350: execution configuration fields.
    tool?: string;
    provider?: string;
    hosting?: string;
    // #199: optional connector binding bundled into the create-unit call.
    // When supplied, the server creates the unit AND binds it
    // transactionally — a binding failure rolls back the whole creation.
    // The server accepts typeId, typeSlug, or both (at least one required);
    // on the wire we pass the zero-GUID as a sentinel when the caller only
    // knows the slug.
    connector?: UnitConnectorBindingRequest;
    // Review feedback on #744: every unit must carry a parent at create
    // time. The wizard currently produces only top-level units (the
    // parent-unit picker is a future UI surface), so the client stamps
    // `isTopLevel=true` by default when the caller does not opt into
    // `parentUnitIds` explicitly.
    parentUnitIds?: string[];
    isTopLevel?: boolean;
  }) =>
    unwrap(
      await fetchClient.POST("/api/v1/tenant/units", {
        body: withDefaultParentParent(body),
      }),
    ),
  createUnitFromYaml: async (body: CreateUnitFromYamlRequest) =>
    unwrap(
      await fetchClient.POST("/api/v1/tenant/units/from-yaml", {
        body: withDefaultParentParent(body),
      }),
    ),
  createUnitFromTemplate: async (body: CreateUnitFromTemplateRequest) =>
    unwrap(
      await fetchClient.POST("/api/v1/tenant/units/from-template", {
        body: withDefaultParentParent(body),
      }),
    ),
  listUnitTemplates: async () =>
    unwrap(await fetchClient.GET("/api/v1/tenant/packages/templates")),
  // Package browse (#395 / PR-PLAT-PKG-1). The /packages and
  // /packages/{name} endpoints are the same data the CLI's
  // `spring package list` / `spring package show` consume, keeping
  // CLI and portal at parity per CONVENTIONS.md § ui-cli-parity.
  listPackages: async () =>
    unwrap(await fetchClient.GET("/api/v1/tenant/packages")),
  getPackage: async (name: string) => {
    // Surface 404 as null so the detail page can render a clean
    // "not found" state instead of bubbling an ApiError up to the
    // error boundary.
    const result = await fetchClient.GET("/api/v1/tenant/packages/{name}", {
      params: { path: { name } },
    });
    if (result.response.status === 404) {
      return null;
    }
    return unwrap(result);
  },
  getUnitTemplate: async (pkg: string, name: string) => {
    const result = await fetchClient.GET(
      "/api/v1/tenant/packages/{package}/templates/{name}",
      { params: { path: { package: pkg, name } } },
    );
    if (result.response.status === 404) {
      return null;
    }
    return unwrap(result);
  },
  updateUnit: async (
    id: string,
    patch: Partial<{
      displayName: string;
      description: string;
      model: string;
      color: string;
    }>,
  ) =>
    unwrap(
      await fetchClient.PATCH("/api/v1/tenant/units/{id}", {
        params: { path: { id } },
        body: patch,
      }),
    ),
  startUnit: async (id: string) =>
    unwrap(
      await fetchClient.POST("/api/v1/tenant/units/{id}/start", {
        params: { path: { id } },
      }),
    ),
  stopUnit: async (id: string) =>
    unwrap(
      await fetchClient.POST("/api/v1/tenant/units/{id}/stop", {
        params: { path: { id } },
      }),
    ),
  getUnitReadiness: async (id: string) =>
    unwrap(
      await fetchClient.GET("/api/v1/tenant/units/{id}/readiness", {
        params: { path: { id } },
      }),
    ),
  // #1137: `force` skips the API's lifecycle-status gate and tombstones
  // the unit even from non-terminal states (Validating, Starting, Running,
  // Stopping). The portal uses it as a recovery path triggered by the
  // confirmation dialog after a regular delete returns 409 with a
  // `forceHint` extension — never as the default delete action.
  deleteUnit: async (
    id: string,
    options?: { force?: boolean },
  ): Promise<void> => {
    assertOk(
      await fetchClient.DELETE("/api/v1/tenant/units/{id}", {
        params: {
          path: { id },
          query: options?.force ? { force: true } : undefined,
        },
      }),
    );
  },
  addMember: async (
    unitId: string,
    memberScheme: string,
    memberPath: string,
  ): Promise<void> => {
    assertOk(
      await fetchClient.POST("/api/v1/tenant/units/{id}/members", {
        params: { path: { id: unitId } },
        body: { memberAddress: { scheme: memberScheme, path: memberPath } },
      }),
    );
  },
  removeMember: async (unitId: string, memberId: string): Promise<void> => {
    assertOk(
      await fetchClient.DELETE("/api/v1/tenant/units/{id}/members/{memberId}", {
        params: { path: { id: unitId, memberId } },
      }),
    );
  },

  // Unit-scoped agent routes — assign / unassign maintain the
  // agent.parentUnit ↔ unit.members invariant in one place. Per-field
  // edits go through updateAgentMetadata (agent-scoped) because the
  // fields are owned by the agent, not by the unit.
  listUnitAgents: async (unitId: string) =>
    unwrap(
      await fetchClient.GET("/api/v1/tenant/units/{id}/agents", {
        params: { path: { id: unitId } },
      }),
    ),
  assignUnitAgent: async (unitId: string, agentId: string) =>
    unwrap(
      await fetchClient.POST("/api/v1/tenant/units/{id}/agents/{agentId}", {
        params: { path: { id: unitId, agentId } },
      }),
    ),
  unassignUnitAgent: async (
    unitId: string,
    agentId: string,
  ): Promise<void> => {
    assertOk(
      await fetchClient.DELETE("/api/v1/tenant/units/{id}/agents/{agentId}", {
        params: { path: { id: unitId, agentId } },
      }),
    );
  },

  // Membership (M:N) surface (#160 / C2b-1). Assign / unassign above remain
  // the high-level wire-compatible surface; these endpoints expose the
  // per-membership config overrides directly.
  listAgentMemberships: async (agentId: string) =>
    unwrap(
      await fetchClient.GET("/api/v1/tenant/agents/{id}/memberships", {
        params: { path: { id: agentId } },
      }),
    ),
  listUnitMemberships: async (unitId: string) =>
    unwrap(
      await fetchClient.GET("/api/v1/tenant/units/{id}/memberships", {
        params: { path: { id: unitId } },
      }),
    ),
  upsertUnitMembership: async (
    unitId: string,
    agentAddress: string,
    body: {
      model?: string | null;
      specialty?: string | null;
      enabled?: boolean | null;
      executionMode?: "Auto" | "OnDemand" | null;
    },
  ) =>
    unwrap(
      await fetchClient.PUT(
        "/api/v1/tenant/units/{unitId}/memberships/{agentAddress}",
        {
          params: { path: { unitId, agentAddress } },
          body,
        },
      ),
    ),
  deleteUnitMembership: async (
    unitId: string,
    agentAddress: string,
  ): Promise<void> => {
    assertOk(
      await fetchClient.DELETE(
        "/api/v1/tenant/units/{unitId}/memberships/{agentAddress}",
        {
          params: { path: { unitId, agentAddress } },
        },
      ),
    );
  },

  // Costs
  //
  // The three cost endpoints accept optional `from` / `to` query params;
  // omitting both preserves the server default (last 30 days). The portal
  // surfaces expose the window so PR-R4 (#394) can render per-window
  // totals on the dashboard summary card and the detail pages without
  // duplicating the cost query service.
  getAgentCost: async (
    id: string,
    range?: { from?: string; to?: string },
  ) => {
    const query: Record<string, string> = {};
    if (range?.from) query.from = range.from;
    if (range?.to) query.to = range.to;
    return unwrap(
      await fetchClient.GET("/api/v1/tenant/cost/agents/{id}", {
        params: { path: { id }, query: query as never },
      }),
    );
  },
  getUnitCost: async (
    id: string,
    range?: { from?: string; to?: string },
  ) => {
    const query: Record<string, string> = {};
    if (range?.from) query.from = range.from;
    if (range?.to) query.to = range.to;
    return unwrap(
      await fetchClient.GET("/api/v1/tenant/cost/units/{id}", {
        params: { path: { id }, query: query as never },
      }),
    );
  },
  /**
   * Tenant-wide cost rollup, optionally windowed. Powers the summary
   * card on the main dashboard (today / 7d / 30d totals, PR-R4).
   */
  getTenantCost: async (
    range?: { from?: string; to?: string },
  ) => {
    const query: Record<string, string> = {};
    if (range?.from) query.from = range.from;
    if (range?.to) query.to = range.to;
    return unwrap(
      await fetchClient.GET("/api/v1/tenant/cost/tenant", {
        params: { query: query as never },
      }),
    );
  },
  /**
   * Tenant cost time-series (V21-tenant-cost-timeseries, #916). Zero-filled,
   * bucketed, shared by the `/budgets` sparkline and the forthcoming
   * analytics chart (#910). Server defaults: `window=30d`, `bucket=1d`.
   * Valid buckets are `1h`, `1d`, `7d`; window must be <= `90d`. The server
   * emits an RFC 7807 400 on unparseable/unknown values — we surface those
   * as ApiError verbatim so call sites treat the hook as "trust the
   * server's validation".
   */
  getTenantCostTimeseries: async (
    params?: { window?: string; bucket?: string },
  ) => {
    const query: Record<string, string> = {};
    if (params?.window) query.window = params.window;
    if (params?.bucket) query.bucket = params.bucket;
    return unwrap(
      await fetchClient.GET("/api/v1/tenant/cost/timeseries", {
        params: { query: query as never },
      }),
    );
  },

  /**
   * Agent cost time-series (#1363). Zero-filled, bucketed.
   * Endpoint: GET /api/v1/tenant/analytics/agents/{id}/cost-timeseries.
   * Server defaults: window=30d, bucket=1d.
   */
  getAgentCostTimeseries: async (
    id: string,
    params?: { window?: string; bucket?: string },
  ) => {
    const query: Record<string, string> = {};
    if (params?.window) query.window = params.window;
    if (params?.bucket) query.bucket = params.bucket;
    return unwrap(
      await fetchClient.GET(
        "/api/v1/tenant/analytics/agents/{id}/cost-timeseries",
        { params: { path: { id }, query: query as never } },
      ),
    );
  },

  /**
   * Unit cost time-series (#1363). Zero-filled, bucketed.
   * Endpoint: GET /api/v1/tenant/analytics/units/{id}/cost-timeseries.
   * Server defaults: window=30d, bucket=1d.
   */
  getUnitCostTimeseries: async (
    id: string,
    params?: { window?: string; bucket?: string },
  ) => {
    const query: Record<string, string> = {};
    if (params?.window) query.window = params.window;
    if (params?.bucket) query.bucket = params.bucket;
    return unwrap(
      await fetchClient.GET(
        "/api/v1/tenant/analytics/units/{id}/cost-timeseries",
        { params: { path: { id }, query: query as never } },
      ),
    );
  },

  /**
   * Agent cost breakdown by model (#1364). Per-model cost for the last
   * server-default window, descending by cost.
   * Endpoint: GET /api/v1/tenant/cost/agents/{id}/breakdown.
   */
  getAgentCostBreakdown: async (id: string) =>
    unwrap(
      await fetchClient.GET("/api/v1/tenant/cost/agents/{id}/breakdown", {
        params: { path: { id } },
      }),
    ),

  // Clones
  getClones: async (agentId: string) =>
    unwrap(
      await fetchClient.GET("/api/v1/tenant/agents/{agentId}/clones", {
        params: { path: { agentId } },
      }),
    ),
  createClone: async (agentId: string, body: CreateCloneRequest) =>
    unwrap(
      await fetchClient.POST("/api/v1/tenant/agents/{agentId}/clones", {
        params: { path: { agentId } },
        body,
      }),
    ),
  deleteClone: async (agentId: string, cloneId: string): Promise<void> => {
    assertOk(
      await fetchClient.DELETE("/api/v1/tenant/agents/{agentId}/clones/{cloneId}", {
        params: { path: { agentId, cloneId } },
      }),
    );
  },

  // Budgets
  getAgentBudget: async (agentId: string) =>
    unwrap(
      await fetchClient.GET("/api/v1/tenant/agents/{agentId}/budget", {
        params: { path: { agentId } },
      }),
    ),
  setAgentBudget: async (agentId: string, body: SetBudgetRequest) =>
    unwrap(
      await fetchClient.PUT("/api/v1/tenant/agents/{agentId}/budget", {
        params: { path: { agentId } },
        body,
      }),
    ),
  getTenantBudget: async () =>
    unwrap(await fetchClient.GET("/api/v1/tenant/budget")),
  setTenantBudget: async (body: SetBudgetRequest) =>
    unwrap(await fetchClient.PUT("/api/v1/tenant/budget", { body })),

  // Activity
  //
  // The query endpoint accepts a set of filter/pagination parameters.
  // Callers pass them as a flat `Record<string, string>`; openapi-fetch
  // expects a structured `params.query`. Pass-through is fine here
  // because the current server binds query strings loosely via
  // `[AsParameters]`.
  queryActivity: async (params?: Record<string, string>) =>
    unwrap(
      await fetchClient.GET("/api/v1/tenant/activity", {
        params: { query: params as never },
      }),
    ),

  // Analytics (#448 / #457).
  //
  // Three verbs live on two endpoints: costs reuse the per-entity + tenant
  // cost endpoints that already shipped (GetAgentCost / GetUnitCost /
  // GetTenantCost), and the two new /analytics endpoints serve throughput
  // and wait-time rollups. The portal + CLI share the same wire shape so
  // `spring analytics {costs,throughput,waits}` round-trips the exact JSON
  // the portal renders.
  getAnalyticsThroughput: async (
    params?: { source?: string; from?: string; to?: string },
  ) => {
    const query: Record<string, string> = {};
    if (params?.source) query.source = params.source;
    if (params?.from) query.from = params.from;
    if (params?.to) query.to = params.to;
    return unwrap(
      await fetchClient.GET("/api/v1/tenant/analytics/throughput", {
        params: { query: query as never },
      }),
    );
  },
  getAnalyticsWaits: async (
    params?: { source?: string; from?: string; to?: string },
  ) => {
    const query: Record<string, string> = {};
    if (params?.source) query.source = params.source;
    if (params?.from) query.from = params.from;
    if (params?.to) query.to = params.to;
    return unwrap(
      await fetchClient.GET("/api/v1/tenant/analytics/waits", {
        params: { query: query as never },
      }),
    );
  },

  // Conversations (#410)
  //
  // Conversations are a projection over the activity event stream;
  // these endpoints are the typed surface the CLI's `spring conversation
  // {list,show,send}` commands use, and the portal mirrors that 1:1 per
  // CONVENTIONS.md § ui-cli-parity.
  listThreads: async (filters?: ThreadListFilters) => {
    // The OpenAPI binder uses TitleCase property names (Unit, Agent,
    // Status, Participant, Limit) for [AsParameters] queries. Translate
    // the camelCase shape we expose to call sites.
    const query: Record<string, string | number> = {};
    if (filters?.unit) query.Unit = filters.unit;
    if (filters?.agent) query.Agent = filters.agent;
    if (filters?.status) query.Status = filters.status;
    if (filters?.participant) query.Participant = filters.participant;
    if (filters?.limit !== undefined) query.Limit = filters.limit;
    return unwrap(
      await fetchClient.GET("/api/v1/tenant/threads", {
        params: { query: query as never },
      }),
    );
  },
  getThread: async (id: string) => {
    const result = await fetchClient.GET("/api/v1/tenant/threads/{id}", {
      params: { path: { id } },
    });
    if (result.response.status === 404) {
      return null;
    }
    return unwrap(result);
  },
  sendThreadMessage: async (
    id: string,
    body: ThreadMessageRequest,
  ) =>
    unwrap(
      await fetchClient.POST("/api/v1/tenant/threads/{id}/messages", {
        params: { path: { id } },
        body,
      }),
    ),
  /**
   * Free-form message send (#985). POSTs to `/api/v1/messages` which
   * routes through the MessageRouter and, for Domain messages without a
   * supplied `threadId`, auto-generates a fresh UUID that's
   * returned on `MessageResponse.threadId`. The portal's
   * "+ New conversation" affordance (#980 item 2) uses this so the user
   * lands on a brand-new thread after a successful send.
   */
  sendMessage: async (body: SendMessageRequest): Promise<MessageResponse> =>
    unwrap(
      await fetchClient.POST("/api/v1/tenant/messages", {
        body,
      }),
    ),
  listInbox: async () => unwrap(await fetchClient.GET("/api/v1/tenant/inbox")),

  /**
   * POST /api/v1/tenant/inbox/{threadId}/mark-read (#1477). Records now as
   * the read cursor for the thread on the calling human's actor. Returns the
   * updated InboxItem with UnreadCount=0.
   */
  markInboxRead: async (threadId: string) =>
    unwrap(
      await fetchClient.POST(
        "/api/v1/tenant/inbox/{threadId}/mark-read",
        { params: { path: { threadId } } },
      ),
    ),

  // Initiative
  getAgentInitiativePolicy: async (id: string) =>
    unwrap(
      await fetchClient.GET("/api/v1/tenant/agents/{id}/initiative/policy", {
        params: { path: { id } },
      }),
    ),
  setAgentInitiativePolicy: async (
    id: string,
    policy: InitiativePolicy,
  ): Promise<void> => {
    assertOk(
      await fetchClient.PUT("/api/v1/tenant/agents/{id}/initiative/policy", {
        params: { path: { id } },
        body: policy,
      }),
    );
  },
  getAgentInitiativeLevel: async (id: string) =>
    unwrap(
      await fetchClient.GET("/api/v1/tenant/agents/{id}/initiative/level", {
        params: { path: { id } },
      }),
    ),
  getUnitInitiativePolicy: async (id: string) =>
    unwrap(
      await fetchClient.GET("/api/v1/tenant/units/{id}/initiative/policy", {
        params: { path: { id } },
      }),
    ),
  setUnitInitiativePolicy: async (
    id: string,
    policy: InitiativePolicy,
  ): Promise<void> => {
    assertOk(
      await fetchClient.PUT("/api/v1/tenant/units/{id}/initiative/policy", {
        params: { path: { id } },
        body: policy,
      }),
    );
  },

  // Unified unit policy — surfaces all five dimensions
  // (skill / model / cost / executionMode / initiative) at once. This
  // is the endpoint the portal's Policies tab rides (PR-R5 / #411) and
  // is also what `spring unit policy <dim> get|set|clear` (PR-C2 / #473)
  // calls under the hood, so both surfaces round-trip the same shape.
  //
  // `PUT` accepts a fully-populated `UnitPolicyResponse` (the merged
  // shape where only the target dimension changes and the rest is
  // carried through verbatim).
  getUnitPolicy: async (id: string): Promise<UnitPolicyResponse> =>
    unwrap(
      await fetchClient.GET("/api/v1/tenant/units/{id}/policy", {
        params: { path: { id } },
      }),
    ),
  setUnitPolicy: async (
    id: string,
    policy: UnitPolicyResponse,
  ): Promise<UnitPolicyResponse> =>
    unwrap(
      await fetchClient.PUT("/api/v1/tenant/units/{id}/policy", {
        params: { path: { id } },
        body: policy,
      }),
    ),

  // Skills catalog
  listSkills: async () => unwrap(await fetchClient.GET("/api/v1/tenant/skills")),

  // Connectors — generic surface
  listConnectors: async () => unwrap(await fetchClient.GET("/api/v1/tenant/connectors")),
  /**
   * Returns the connector type metadata, or `null` when the slug/id
   * isn't registered. Normalising 404 → null lets the detail page
   * render a clean "not found" state without a try/catch.
   */
  getConnector: async (slugOrId: string) => {
    const result = await fetchClient.GET("/api/v1/tenant/connectors/{slugOrId}", {
      params: { path: { slugOrId } },
    });
    if (result.response.status === 404) {
      return null;
    }
    return unwrap(result);
  },
  /**
   * Fetches a connector's JSON Schema describing its per-unit config
   * body. The endpoint URL lives on `InstalledConnectorResponse.configSchemaUrl`
   * (e.g. `/api/v1/connectors/github/config-schema`); we accept the slug
   * here and assemble the URL so call sites don't have to. Returns `null`
   * when the connector doesn't expose a schema (404 or empty body).
   */
  getConnectorConfigSchema: async (slug: string): Promise<unknown | null> => {
    const resp = await fetch(
      `${BASE}/api/v1/tenant/connectors/${encodeURIComponent(slug)}/config-schema`,
    );
    if (resp.status === 404) {
      return null;
    }
    if (!resp.ok) {
      throw new ApiError(resp.status, resp.statusText, await resp.text());
    }
    const text = await resp.text();
    if (!text.trim()) {
      return null;
    }
    return JSON.parse(text) as unknown;
  },
  /**
   * Returns every unit bound to the given connector type (#520). Replaces
   * the per-unit fan-out that `useConnectorBindings` used to issue after
   * #516 landed — the server now walks the unit directory once and
   * returns one array in a single round-trip. The response carries the
   * unit identity and pointer together so the portal's "Bound units"
   * list, and `spring connector bindings <slug>`, render without a
   * second lookup. 404 is reserved for "unknown connector type"; a
   * registered connector with no bindings returns `[]`.
   */
  listConnectorBindings: async (slugOrId: string) =>
    unwrap(
      await fetchClient.GET("/api/v1/tenant/connectors/{slugOrId}/bindings", {
        params: { path: { slugOrId } },
      }),
    ),
  /**
   * Returns the unit's active connector binding pointer, or `null` when
   * the unit isn't bound. Normalizing 404 → null here keeps call sites
   * from needing a try/catch just to distinguish "no binding" from a real
   * error.
   */
  getUnitConnector: async (unitId: string) => {
    const result = await fetchClient.GET("/api/v1/tenant/units/{id}/connector", {
      params: { path: { id: unitId } },
    });
    if (result.response.status === 404) {
      return null;
    }
    return unwrap(result);
  },
  clearUnitConnector: async (unitId: string): Promise<void> => {
    assertOk(
      await fetchClient.DELETE("/api/v1/tenant/units/{id}/connector", {
        params: { path: { id: unitId } },
      }),
    );
  },

  // Connectors — GitHub typed surface
  listGitHubInstallations: async () =>
    unwrap(
      await fetchClient.GET(
        "/api/v1/tenant/connectors/github/actions/list-installations",
      ),
    ),
  /**
   * Aggregated repository list (#1133). One row per repo the GitHub App
   * can see, collapsed across every visible installation; the wizard
   * binds its single Repository dropdown to this rather than asking the
   * user to type owner+repo and pick an installation. Each row carries
   * the installation id back so the connector never has to re-resolve
   * `(owner, repo) → installation`.
   */
  listGitHubRepositories: async () =>
    unwrap(
      await fetchClient.GET(
        "/api/v1/tenant/connectors/github/actions/list-repositories",
      ),
    ),
  /**
   * Lists the collaborators on a single repository (#1133). The wizard's
   * Reviewer dropdown calls this whenever the repo selection changes;
   * the installation id is required so the connector mints the right
   * token without doing a repo-to-installation resolve every time.
   */
  listGitHubCollaborators: async (
    installationId: number,
    owner: string,
    repo: string,
  ) =>
    unwrap(
      await fetchClient.GET(
        "/api/v1/tenant/connectors/github/actions/list-collaborators",
        {
          params: {
            query: { installation_id: installationId, owner, repo } as never,
          },
        },
      ),
    ),
  getGitHubInstallUrl: async () =>
    unwrap(
      await fetchClient.GET("/api/v1/tenant/connectors/github/actions/install-url"),
    ),
  getUnitGitHubConfig: async (unitId: string) => {
    const result = await fetchClient.GET(
      "/api/v1/tenant/connectors/github/units/{unitId}/config",
      { params: { path: { unitId } } },
    );
    if (result.response.status === 404) {
      return null;
    }
    return unwrap(result);
  },
  putUnitGitHubConfig: async (
    unitId: string,
    body: UnitGitHubConfigRequest,
  ) =>
    unwrap(
      await fetchClient.PUT(
        "/api/v1/tenant/connectors/github/units/{unitId}/config",
        { params: { path: { unitId } }, body },
      ),
    ),
  // Unit-scoped secrets (#122).
  //
  // The API never returns plaintext values. `createUnitSecret` is the
  // only path through which a plaintext leaves this client — the
  // browser POSTs the value once and never holds it beyond the fetch.
  listUnitSecrets: async (unitId: string) =>
    unwrap(
      await fetchClient.GET("/api/v1/tenant/units/{id}/secrets", {
        params: { path: { id: unitId } },
      }),
    ),
  createUnitSecret: async (unitId: string, body: CreateSecretRequest) =>
    unwrap(
      await fetchClient.POST("/api/v1/tenant/units/{id}/secrets", {
        params: { path: { id: unitId } },
        body,
      }),
    ),
  deleteUnitSecret: async (unitId: string, name: string): Promise<void> => {
    assertOk(
      await fetchClient.DELETE("/api/v1/tenant/units/{id}/secrets/{name}", {
        params: { path: { id: unitId, name } },
      }),
    );
  },
  // Tenant-scoped secrets (#615). Tenant-default credentials — LLM API
  // keys and anything else a tenant wants to share across its units —
  // live here. Units inherit from this scope automatically unless they
  // register the same-name secret at unit scope (Secrets tab on a
  // unit). Powers the Tenant defaults panel in the Settings drawer.
  listTenantSecrets: async () =>
    unwrap(await fetchClient.GET("/api/v1/tenant/secrets", {})),
  createTenantSecret: async (body: CreateSecretRequest) =>
    unwrap(
      await fetchClient.POST("/api/v1/tenant/secrets", { body }),
    ),
  rotateTenantSecret: async (name: string, body: CreateSecretRequest) =>
    unwrap(
      await fetchClient.PUT("/api/v1/tenant/secrets/{name}", {
        params: { path: { name } },
        body,
      }),
    ),
  deleteTenantSecret: async (name: string): Promise<void> => {
    assertOk(
      await fetchClient.DELETE("/api/v1/tenant/secrets/{name}", {
        params: { path: { name } },
      }),
    );
  },

  // Unit boundary (#413). The GET endpoint always returns the empty
  // shape (no 404) when a unit has never had a boundary persisted, so
  // there's no 404-normalisation to do here — the caller either gets
  // the current boundary or an ApiError.
  getUnitBoundary: async (unitId: string): Promise<UnitBoundaryResponse> =>
    unwrap(
      await fetchClient.GET("/api/v1/tenant/units/{id}/boundary", {
        params: { path: { id: unitId } },
      }),
    ),
  setUnitBoundary: async (
    unitId: string,
    body: UnitBoundaryResponse,
  ): Promise<UnitBoundaryResponse> =>
    unwrap(
      await fetchClient.PUT("/api/v1/tenant/units/{id}/boundary", {
        params: { path: { id: unitId } },
        body,
      }),
    ),
  clearUnitBoundary: async (unitId: string): Promise<void> => {
    assertOk(
      await fetchClient.DELETE("/api/v1/tenant/units/{id}/boundary", {
        params: { path: { id: unitId } },
      }),
    );
  },

  // Unit orchestration (#606). Dedicated surface for the manifest-
  // persisted `orchestration.strategy` key — the Orchestration tab's
  // strategy selector writes through here so the dropdown becomes
  // directly editable. The GET endpoint always returns the empty shape
  // when the slot has never been set, so there is no 404 to normalise.
  getUnitOrchestration: async (
    unitId: string,
  ): Promise<UnitOrchestrationResponse> =>
    unwrap(
      await fetchClient.GET("/api/v1/tenant/units/{id}/orchestration", {
        params: { path: { id: unitId } },
      }),
    ),
  setUnitOrchestration: async (
    unitId: string,
    body: UnitOrchestrationResponse,
  ): Promise<UnitOrchestrationResponse> =>
    unwrap(
      await fetchClient.PUT("/api/v1/tenant/units/{id}/orchestration", {
        params: { path: { id: unitId } },
        body,
      }),
    ),
  clearUnitOrchestration: async (unitId: string): Promise<void> => {
    assertOk(
      await fetchClient.DELETE("/api/v1/tenant/units/{id}/orchestration", {
        params: { path: { id: unitId } },
      }),
    );
  },

  // Unit execution defaults (#601 / #603 / #409 B-wide, backend PR #628).
  // Dedicated surface for the manifest-persisted `execution:` block that
  // member agents inherit at dispatch time. PUT semantics are
  // **partial update** — a non-null field replaces the corresponding
  // slot, null leaves the existing value alone. The portal's per-field
  // Clear button implements the "unset this one field" intent by reading
  // the current block, clearing the field, and re-PUTing with the
  // remaining fields (or DELETE if every field ends up null).
  getUnitExecution: async (unitId: string): Promise<UnitExecutionResponse> =>
    unwrap(
      await fetchClient.GET("/api/v1/tenant/units/{id}/execution", {
        params: { path: { id: unitId } },
      }),
    ),
  setUnitExecution: async (
    unitId: string,
    body: UnitExecutionResponse,
  ): Promise<UnitExecutionResponse> =>
    unwrap(
      await fetchClient.PUT("/api/v1/tenant/units/{id}/execution", {
        params: { path: { id: unitId } },
        body,
      }),
    ),
  clearUnitExecution: async (unitId: string): Promise<void> => {
    assertOk(
      await fetchClient.DELETE("/api/v1/tenant/units/{id}/execution", {
        params: { path: { id: unitId } },
      }),
    );
  },

  // Agent execution (#601 / #603 / #409 B-wide, backend PR #628). The
  // agent-owned `execution:` block carries the same five fields as the
  // unit plus the agent-exclusive `hosting` slot. The response shape is
  // the agent's **own declared** block — inherited unit defaults are
  // merged at dispatch time, not by this endpoint. The portal's
  // `inherited from unit` indicator overlays the value it reads from
  // `getUnitExecution`.
  getAgentExecution: async (
    agentId: string,
  ): Promise<AgentExecutionResponse> =>
    unwrap(
      await fetchClient.GET("/api/v1/tenant/agents/{id}/execution", {
        params: { path: { id: agentId } },
      }),
    ),
  setAgentExecution: async (
    agentId: string,
    body: AgentExecutionResponse,
  ): Promise<AgentExecutionResponse> =>
    unwrap(
      await fetchClient.PUT("/api/v1/tenant/agents/{id}/execution", {
        params: { path: { id: agentId } },
        body,
      }),
    ),
  clearAgentExecution: async (agentId: string): Promise<void> => {
    assertOk(
      await fetchClient.DELETE("/api/v1/tenant/agents/{id}/execution", {
        params: { path: { id: agentId } },
      }),
    );
  },

  // Expertise directory (#412 / #486). Agents and units expose the
  // same get/set shape for their "own" expertise list; units also
  // expose a read-only aggregated view that recursively composes every
  // descendant's expertise (PR #487). The CLI mirror is
  // `spring {agent|unit} expertise {get|set[|aggregated]}`.
  getAgentExpertise: async (id: string): Promise<ExpertiseDomainDto[]> => {
    const res = unwrap(
      await fetchClient.GET("/api/v1/tenant/agents/{id}/expertise", {
        params: { path: { id } },
      }),
    );
    return res.domains ?? [];
  },
  setAgentExpertise: async (
    id: string,
    domains: ExpertiseDomainDto[],
  ): Promise<ExpertiseDomainDto[]> => {
    const res = unwrap(
      await fetchClient.PUT("/api/v1/tenant/agents/{id}/expertise", {
        params: { path: { id } },
        body: { domains },
      }),
    );
    return res.domains ?? [];
  },
  getUnitOwnExpertise: async (id: string): Promise<ExpertiseDomainDto[]> => {
    const res = unwrap(
      await fetchClient.GET("/api/v1/tenant/units/{id}/expertise/own", {
        params: { path: { id } },
      }),
    );
    return res.domains ?? [];
  },
  setUnitOwnExpertise: async (
    id: string,
    domains: ExpertiseDomainDto[],
  ): Promise<ExpertiseDomainDto[]> => {
    const res = unwrap(
      await fetchClient.PUT("/api/v1/tenant/units/{id}/expertise/own", {
        params: { path: { id } },
        body: { domains },
      }),
    );
    return res.domains ?? [];
  },
  getUnitAggregatedExpertise: async (id: string) =>
    unwrap(
      await fetchClient.GET("/api/v1/tenant/units/{id}/expertise", {
        params: { path: { id } },
      }),
    ),

  // Expertise directory search (#542). Lexical / full-text ranked search
  // over every per-entity expertise declaration, plus aggregated-coverage
  // hits through unit projections. Shared between the portal's /directory
  // search box and the CLI's `spring directory search` verb.
  searchDirectory: async (
    body: DirectorySearchRequest,
  ): Promise<DirectorySearchResponse> =>
    unwrap(
      await fetchClient.POST("/api/v1/tenant/directory/search", { body }),
    ) as DirectorySearchResponse,

  // Platform metadata (#451). Anonymous read — the About panel and
  // `spring platform info` both point here so version reporting can't
  // drift between UI and CLI.
  getPlatformInfo: async () =>
    unwrap(await fetchClient.GET("/api/v1/platform/info")),

  // Auth — the portal's Settings → Account panel mirrors the CLI's
  // `spring auth token {create,list,revoke}` surface 1:1.
  //
  // Security contract for createAuthToken:
  //  - The plaintext token is returned ONCE in CreateTokenResponse.token.
  //  - The caller must display it exactly once and scrub it from state on
  //    dismiss (one-shot reveal pattern, #557).
  //  - The list endpoint returns only TokenResponse (name + dates); the
  //    plaintext is NEVER returned by any other endpoint.
  getCurrentUser: async () =>
    unwrap(await fetchClient.GET("/api/v1/tenant/auth/me")),
  listAuthTokens: async () =>
    unwrap(await fetchClient.GET("/api/v1/tenant/auth/tokens")),
  /**
   * Create a new API token. Mirrors `spring auth token create <name>`.
   * Returns the plaintext token exactly once — the caller must show it
   * to the operator and scrub it from state on dismiss.
   */
  createAuthToken: async (body: CreateTokenRequest): Promise<CreateTokenResponse> =>
    unwrap(
      await fetchClient.POST("/api/v1/tenant/auth/tokens", { body }),
    ) as CreateTokenResponse,
  /**
   * Revoke an API token by name. Mirrors `spring auth token revoke <name>`.
   * Returns 204 on success; throws ApiError on 404 (already revoked).
   */
  revokeAuthToken: async (name: string): Promise<void> => {
    assertOk(
      await fetchClient.DELETE("/api/v1/tenant/auth/tokens/{name}", {
        params: { path: { name } },
      }),
    );
  },

  // Ollama model discovery (#350) — C1.2b retired the legacy
  // /api/v1/ollama/models route. Callers now read the per-runtime
  // tenant-install model catalogue via getAgentRuntimeModels("ollama"),
  // which returns the union of the runtime's seed catalogue and any
  // tenant-configured overrides. Kept as a thin shim to preserve the
  // existing portal hook shape; the wizard's hook layer maps the
  // AgentRuntimeModelResponse to the legacy `{name, size, modifiedAt}`
  // tuple it consumed.
  listOllamaModels: async (): Promise<
    { name: string; size: number; modifiedAt: string | null }[]
  > => {
    const models = await api.getAgentRuntimeModels("ollama");
    return models.map((m) => ({
      name: m.id,
      size: 0,
      modifiedAt: null,
    }));
  },

  // Provider credential-status probe (#598). The server answers whether
  // the currently-selected LLM provider is usable — credentials for
  // Anthropic/OpenAI/Google, endpoint reachability for Ollama. The
  // response is a narrow `{ resolvable, source, suggestion }` shape and
  // NEVER contains the key material itself; see the endpoint doc-comment
  // for the invariant.
  getProviderCredentialStatus: async (
    provider: string,
    agentImage?: string,
  ): Promise<import("./types").ProviderCredentialStatusResponse> => {
    // #1397: pass the chosen agent image so the server can reference it in
    // the format-rejected error message. The parameter is optional — older
    // callers that don't supply it get the same response as before.
    const url = new URL(
      `${BASE}/api/v1/platform/credentials/${encodeURIComponent(provider)}/status`,
    );
    if (agentImage) {
      url.searchParams.set("agentImage", agentImage);
    }
    const resp = await fetch(url.toString());
    if (!resp.ok) {
      throw new ApiError(resp.status, resp.statusText, await resp.text());
    }
    return (await resp.json()) as import("./types").ProviderCredentialStatusResponse;
  },

  // Tenant-installed agent runtimes (#690). Feeds the unit-creation
  // wizard's provider + model dropdowns: the wizard reads the available
  // runtimes from this endpoint, then per-runtime models from
  // `getAgentRuntimeModels`. Host-side credential validation was retired
  // in T-03 (#945) and the transitional stub `validateAgentRuntimeCredential`
  // was removed in T-07 (#949) — validation now runs as a backend Dapr
  // workflow and the outcome is reported through the detail-page
  // Validation panel via SSE + the unit's `lastValidationError`.
  listAgentRuntimes: async (): Promise<
    import("./types").InstalledAgentRuntimeResponse[]
  > =>
    unwrap(await fetchClient.GET("/api/v1/tenant/agent-runtimes/installs")) as import("./types").InstalledAgentRuntimeResponse[],

  getAgentRuntimeModels: async (
    id: string,
  ): Promise<import("./types").AgentRuntimeModelResponse[]> =>
    unwrap(
      await fetchClient.GET("/api/v1/tenant/agent-runtimes/installs/{id}/models", {
        params: { path: { id } },
      }),
    ) as import("./types").AgentRuntimeModelResponse[],

  // T-05 (#947): re-run backend validation for a unit that's in
  // `Error` or `Stopped`. POST is fire-and-forget — the workflow
  // transitions the unit into `Validating` and the detail page
  // observes progress via `ValidationProgress` SSE events. Returns
  // 202 on success; a 409 from `Running / Starting / Stopping / Draft`
  // is bubbled as an ApiError by `assertOk`.
  revalidateUnit: async (id: string): Promise<void> => {
    assertOk(
      await fetchClient.POST("/api/v1/tenant/units/{id}/revalidate", {
        params: { path: { id } },
      }),
    );
  },

  // Credential health (#691). Read-only inspection of the persistent
  // credential status the watchdog + accept-time validation write to. The
  // admin portal views (`/admin/agent-runtimes`, `/admin/connectors`) ride
  // these; mutation stays CLI-only per the AGENTS.md carve-out. 404
  // normalises to null so the caller can render a "no signal yet" row
  // without a try/catch.
  getAgentRuntimeCredentialHealth: async (
    id: string,
    secretName?: string,
  ): Promise<import("./types").CredentialHealthResponse | null> => {
    const query: Record<string, string> = {};
    if (secretName) query.secretName = secretName;
    const result = await fetchClient.GET(
      "/api/v1/tenant/agent-runtimes/installs/{id}/credential-health",
      {
        params: { path: { id }, query: query as never },
      },
    );
    if (result.response.status === 404) {
      return null;
    }
    return unwrap(result) as import("./types").CredentialHealthResponse;
  },

  getConnectorCredentialHealth: async (
    slugOrId: string,
    secretName?: string,
  ): Promise<import("./types").CredentialHealthResponse | null> => {
    const query: Record<string, string> = {};
    if (secretName) query.secretName = secretName;
    const result = await fetchClient.GET(
      "/api/v1/tenant/connectors/{slugOrId}/credential-health",
      {
        params: { path: { slugOrId }, query: query as never },
      },
    );
    if (result.response.status === 404) {
      return null;
    }
    return unwrap(result) as import("./types").CredentialHealthResponse;
  },

  // ---------------------------------------------------------------------------
  // Persistent cloning policy (PR-PLAT-CLONE-1, #416 / #534).
  // Per-agent scope: GET/PUT/DELETE /api/v1/tenant/agents/{id}/cloning-policy.
  // Tenant-wide scope: GET/PUT/DELETE /api/v1/tenant/cloning-policy.
  // ---------------------------------------------------------------------------

  getAgentCloningPolicy: async (id: string): Promise<AgentCloningPolicyResponse> =>
    unwrap(
      await fetchClient.GET("/api/v1/tenant/agents/{id}/cloning-policy", {
        params: { path: { id } },
      }),
    ),

  setAgentCloningPolicy: async (
    id: string,
    policy: AgentCloningPolicyResponse,
  ): Promise<AgentCloningPolicyResponse> =>
    unwrap(
      await fetchClient.PUT("/api/v1/tenant/agents/{id}/cloning-policy", {
        params: { path: { id } },
        body: policy,
      }),
    ),

  deleteAgentCloningPolicy: async (id: string): Promise<void> => {
    await fetchClient.DELETE("/api/v1/tenant/agents/{id}/cloning-policy", {
      params: { path: { id } },
    });
  },

  getTenantCloningPolicy: async (): Promise<AgentCloningPolicyResponse> =>
    unwrap(await fetchClient.GET("/api/v1/tenant/cloning-policy", {})),

  setTenantCloningPolicy: async (
    policy: AgentCloningPolicyResponse,
  ): Promise<AgentCloningPolicyResponse> =>
    unwrap(
      await fetchClient.PUT("/api/v1/tenant/cloning-policy", { body: policy }),
    ),

  deleteTenantCloningPolicy: async (): Promise<void> => {
    await fetchClient.DELETE("/api/v1/tenant/cloning-policy", {});
  },

  // Tenant tree (SVR-tenant-tree, umbrella #815). Single-payload tenant
  // snapshot for the Explorer surface at `/units`.
  getTenantTree: async () =>
    unwrap(await fetchClient.GET("/api/v1/tenant/tree", {})),

  // Memories inspector (SVR-memories, umbrella #815). v2.0 stub —
  // always returns empty short-term + long-term lists.
  getUnitMemories: async (id: string) =>
    unwrap(
      await fetchClient.GET("/api/v1/tenant/units/{id}/memories", {
        params: { path: { id } },
      }),
    ),
  getAgentMemories: async (id: string) =>
    unwrap(
      await fetchClient.GET("/api/v1/tenant/agents/{id}/memories", {
        params: { path: { id } },
      }),
    ),
};
