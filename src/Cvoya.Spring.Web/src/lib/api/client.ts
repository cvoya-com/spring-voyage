import createClient from "openapi-fetch";

import type { paths } from "./schema";
import type {
  AgentDetailResponse,
  ConversationListFilters,
  ConversationMessageRequest,
  CreateCloneRequest,
  CreateSecretRequest,
  CreateUnitFromTemplateRequest,
  CreateUnitFromYamlRequest,
  DashboardSummary,
  InitiativePolicy,
  SetBudgetRequest,
  UnitConnectorBindingRequest,
  UnitGitHubConfigRequest,
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
class ApiError extends Error {
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

export const api = {
  // Dashboard
  getDashboardSummary: async (): Promise<DashboardSummary> => {
    const resp = await fetch(`${BASE}/api/v1/dashboard/summary`);
    if (!resp.ok) {
      throw new ApiError(resp.status, resp.statusText, await resp.text());
    }
    return resp.json() as Promise<DashboardSummary>;
  },
  getDashboardAgents: async () =>
    unwrap(await fetchClient.GET("/api/v1/dashboard/agents")),
  getDashboardUnits: async () =>
    unwrap(await fetchClient.GET("/api/v1/dashboard/units")),
  getDashboardCosts: async () =>
    unwrap(await fetchClient.GET("/api/v1/dashboard/costs")),

  // Agents
  listAgents: async () => unwrap(await fetchClient.GET("/api/v1/agents")),
  // The generated type for GET /api/v1/agents/{id} is AgentDetailResponse;
  // the handler falls back to returning `{ agent, status: null }` when the
  // StatusQuery to the actor fails. Existing call sites expect
  // AgentDetailResponse, so surface that directly.
  getAgent: async (id: string): Promise<AgentDetailResponse> =>
    unwrap(
      await fetchClient.GET("/api/v1/agents/{id}", {
        params: { path: { id } },
      }),
    ) as AgentDetailResponse,
  updateAgentMetadata: async (id: string, patch: UpdateAgentMetadataRequest) =>
    unwrap(
      await fetchClient.PATCH("/api/v1/agents/{id}", {
        params: { path: { id } },
        body: patch,
      }),
    ),
  getAgentSkills: async (id: string) =>
    unwrap(
      await fetchClient.GET("/api/v1/agents/{id}/skills", {
        params: { path: { id } },
      }),
    ),
  setAgentSkills: async (id: string, skills: string[]) =>
    unwrap(
      await fetchClient.PUT("/api/v1/agents/{id}/skills", {
        params: { path: { id } },
        body: { skills },
      }),
    ),
  deleteAgent: async (id: string): Promise<void> => {
    assertOk(
      await fetchClient.DELETE("/api/v1/agents/{id}", {
        params: { path: { id } },
      }),
    );
  },

  // Units
  //
  // Lightweight list of every unit the caller can see. Used by the
  // sub-units picker (#352) to offer candidates when adding a child
  // unit to a parent.
  listUnits: async () => unwrap(await fetchClient.GET("/api/v1/units")),
  // Detailed unit read — includes Members and raw status payload. Used by
  // the legacy query-string detail view under /units?id=... and still
  // useful for anything that needs the members/details blob.
  getUnitDetail: async (id: string) =>
    unwrap(
      await fetchClient.GET("/api/v1/units/{id}", {
        params: { path: { id } },
      }),
    ),
  // Lightweight unit read that returns the unit envelope only. Used by
  // the /units/[id] config page where the tabs shell pulls data
  // independently.
  getUnit: async (id: string): Promise<UnitResponse> => {
    const detail = unwrap(
      await fetchClient.GET("/api/v1/units/{id}", {
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
  }) =>
    unwrap(
      await fetchClient.POST("/api/v1/units", { body }),
    ),
  createUnitFromYaml: async (body: CreateUnitFromYamlRequest) =>
    unwrap(
      await fetchClient.POST("/api/v1/units/from-yaml", { body }),
    ),
  createUnitFromTemplate: async (body: CreateUnitFromTemplateRequest) =>
    unwrap(
      await fetchClient.POST("/api/v1/units/from-template", { body }),
    ),
  listUnitTemplates: async () =>
    unwrap(await fetchClient.GET("/api/v1/packages/templates")),
  // Package browse (#395 / PR-PLAT-PKG-1). The /packages and
  // /packages/{name} endpoints are the same data the CLI's
  // `spring package list` / `spring package show` consume, keeping
  // CLI and portal at parity per CONVENTIONS.md § ui-cli-parity.
  listPackages: async () =>
    unwrap(await fetchClient.GET("/api/v1/packages")),
  getPackage: async (name: string) => {
    // Surface 404 as null so the detail page can render a clean
    // "not found" state instead of bubbling an ApiError up to the
    // error boundary.
    const result = await fetchClient.GET("/api/v1/packages/{name}", {
      params: { path: { name } },
    });
    if (result.response.status === 404) {
      return null;
    }
    return unwrap(result);
  },
  getUnitTemplate: async (pkg: string, name: string) => {
    const result = await fetchClient.GET(
      "/api/v1/packages/{package}/templates/{name}",
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
      await fetchClient.PATCH("/api/v1/units/{id}", {
        params: { path: { id } },
        body: patch,
      }),
    ),
  startUnit: async (id: string) =>
    unwrap(
      await fetchClient.POST("/api/v1/units/{id}/start", {
        params: { path: { id } },
      }),
    ),
  stopUnit: async (id: string) =>
    unwrap(
      await fetchClient.POST("/api/v1/units/{id}/stop", {
        params: { path: { id } },
      }),
    ),
  getUnitReadiness: async (id: string) =>
    unwrap(
      await fetchClient.GET("/api/v1/units/{id}/readiness", {
        params: { path: { id } },
      }),
    ),
  deleteUnit: async (id: string): Promise<void> => {
    assertOk(
      await fetchClient.DELETE("/api/v1/units/{id}", {
        params: { path: { id } },
      }),
    );
  },
  addMember: async (
    unitId: string,
    memberScheme: string,
    memberPath: string,
  ): Promise<void> => {
    assertOk(
      await fetchClient.POST("/api/v1/units/{id}/members", {
        params: { path: { id: unitId } },
        body: { memberAddress: { scheme: memberScheme, path: memberPath } },
      }),
    );
  },
  removeMember: async (unitId: string, memberId: string): Promise<void> => {
    assertOk(
      await fetchClient.DELETE("/api/v1/units/{id}/members/{memberId}", {
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
      await fetchClient.GET("/api/v1/units/{id}/agents", {
        params: { path: { id: unitId } },
      }),
    ),
  assignUnitAgent: async (unitId: string, agentId: string) =>
    unwrap(
      await fetchClient.POST("/api/v1/units/{id}/agents/{agentId}", {
        params: { path: { id: unitId, agentId } },
      }),
    ),
  unassignUnitAgent: async (
    unitId: string,
    agentId: string,
  ): Promise<void> => {
    assertOk(
      await fetchClient.DELETE("/api/v1/units/{id}/agents/{agentId}", {
        params: { path: { id: unitId, agentId } },
      }),
    );
  },

  // Membership (M:N) surface (#160 / C2b-1). Assign / unassign above remain
  // the high-level wire-compatible surface; these endpoints expose the
  // per-membership config overrides directly.
  listAgentMemberships: async (agentId: string) =>
    unwrap(
      await fetchClient.GET("/api/v1/agents/{id}/memberships", {
        params: { path: { id: agentId } },
      }),
    ),
  listUnitMemberships: async (unitId: string) =>
    unwrap(
      await fetchClient.GET("/api/v1/units/{id}/memberships", {
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
        "/api/v1/units/{unitId}/memberships/{agentAddress}",
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
        "/api/v1/units/{unitId}/memberships/{agentAddress}",
        {
          params: { path: { unitId, agentAddress } },
        },
      ),
    );
  },

  // Costs
  getAgentCost: async (id: string) =>
    unwrap(
      await fetchClient.GET("/api/v1/costs/agents/{id}", {
        params: { path: { id } },
      }),
    ),
  getUnitCost: async (id: string) =>
    unwrap(
      await fetchClient.GET("/api/v1/costs/units/{id}", {
        params: { path: { id } },
      }),
    ),

  // Clones
  getClones: async (agentId: string) =>
    unwrap(
      await fetchClient.GET("/api/v1/agents/{agentId}/clones", {
        params: { path: { agentId } },
      }),
    ),
  createClone: async (agentId: string, body: CreateCloneRequest) =>
    unwrap(
      await fetchClient.POST("/api/v1/agents/{agentId}/clones", {
        params: { path: { agentId } },
        body,
      }),
    ),
  deleteClone: async (agentId: string, cloneId: string): Promise<void> => {
    assertOk(
      await fetchClient.DELETE("/api/v1/agents/{agentId}/clones/{cloneId}", {
        params: { path: { agentId, cloneId } },
      }),
    );
  },

  // Budgets
  getAgentBudget: async (agentId: string) =>
    unwrap(
      await fetchClient.GET("/api/v1/agents/{agentId}/budget", {
        params: { path: { agentId } },
      }),
    ),
  setAgentBudget: async (agentId: string, body: SetBudgetRequest) =>
    unwrap(
      await fetchClient.PUT("/api/v1/agents/{agentId}/budget", {
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
      await fetchClient.GET("/api/v1/activity", {
        params: { query: params as never },
      }),
    ),

  // Conversations (#410)
  //
  // Conversations are a projection over the activity event stream;
  // these endpoints are the typed surface the CLI's `spring conversation
  // {list,show,send}` commands use, and the portal mirrors that 1:1 per
  // CONVENTIONS.md § ui-cli-parity.
  listConversations: async (filters?: ConversationListFilters) => {
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
      await fetchClient.GET("/api/v1/conversations", {
        params: { query: query as never },
      }),
    );
  },
  getConversation: async (id: string) => {
    const result = await fetchClient.GET("/api/v1/conversations/{id}", {
      params: { path: { id } },
    });
    if (result.response.status === 404) {
      return null;
    }
    return unwrap(result);
  },
  sendConversationMessage: async (
    id: string,
    body: ConversationMessageRequest,
  ) =>
    unwrap(
      await fetchClient.POST("/api/v1/conversations/{id}/messages", {
        params: { path: { id } },
        body,
      }),
    ),
  listInbox: async () => unwrap(await fetchClient.GET("/api/v1/inbox")),

  // Initiative
  getAgentInitiativePolicy: async (id: string) =>
    unwrap(
      await fetchClient.GET("/api/v1/agents/{id}/initiative/policy", {
        params: { path: { id } },
      }),
    ),
  setAgentInitiativePolicy: async (
    id: string,
    policy: InitiativePolicy,
  ): Promise<void> => {
    assertOk(
      await fetchClient.PUT("/api/v1/agents/{id}/initiative/policy", {
        params: { path: { id } },
        body: policy,
      }),
    );
  },
  getAgentInitiativeLevel: async (id: string) =>
    unwrap(
      await fetchClient.GET("/api/v1/agents/{id}/initiative/level", {
        params: { path: { id } },
      }),
    ),
  getUnitInitiativePolicy: async (id: string) =>
    unwrap(
      await fetchClient.GET("/api/v1/units/{id}/initiative/policy", {
        params: { path: { id } },
      }),
    ),
  setUnitInitiativePolicy: async (
    id: string,
    policy: InitiativePolicy,
  ): Promise<void> => {
    assertOk(
      await fetchClient.PUT("/api/v1/units/{id}/initiative/policy", {
        params: { path: { id } },
        body: policy,
      }),
    );
  },

  // Skills catalog
  listSkills: async () => unwrap(await fetchClient.GET("/api/v1/skills")),

  // Connectors — generic surface
  listConnectors: async () => unwrap(await fetchClient.GET("/api/v1/connectors")),
  /**
   * Returns the connector type metadata, or `null` when the slug/id
   * isn't registered. Normalising 404 → null lets the detail page
   * render a clean "not found" state without a try/catch.
   */
  getConnector: async (slugOrId: string) => {
    const result = await fetchClient.GET("/api/v1/connectors/{slugOrId}", {
      params: { path: { slugOrId } },
    });
    if (result.response.status === 404) {
      return null;
    }
    return unwrap(result);
  },
  /**
   * Fetches a connector's JSON Schema describing its per-unit config
   * body. The endpoint URL lives on `ConnectorTypeResponse.configSchemaUrl`
   * (e.g. `/api/v1/connectors/github/config-schema`); we accept the slug
   * here and assemble the URL so call sites don't have to. Returns `null`
   * when the connector doesn't expose a schema (404 or empty body).
   */
  getConnectorConfigSchema: async (slug: string): Promise<unknown | null> => {
    const resp = await fetch(
      `${BASE}/api/v1/connectors/${encodeURIComponent(slug)}/config-schema`,
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
   * Returns the unit's active connector binding pointer, or `null` when
   * the unit isn't bound. Normalizing 404 → null here keeps call sites
   * from needing a try/catch just to distinguish "no binding" from a real
   * error.
   */
  getUnitConnector: async (unitId: string) => {
    const result = await fetchClient.GET("/api/v1/units/{id}/connector", {
      params: { path: { id: unitId } },
    });
    if (result.response.status === 404) {
      return null;
    }
    return unwrap(result);
  },
  clearUnitConnector: async (unitId: string): Promise<void> => {
    assertOk(
      await fetchClient.DELETE("/api/v1/units/{id}/connector", {
        params: { path: { id: unitId } },
      }),
    );
  },

  // Connectors — GitHub typed surface
  listGitHubInstallations: async () =>
    unwrap(
      await fetchClient.GET(
        "/api/v1/connectors/github/actions/list-installations",
      ),
    ),
  getGitHubInstallUrl: async () =>
    unwrap(
      await fetchClient.GET("/api/v1/connectors/github/actions/install-url"),
    ),
  getUnitGitHubConfig: async (unitId: string) => {
    const result = await fetchClient.GET(
      "/api/v1/connectors/github/units/{unitId}/config",
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
        "/api/v1/connectors/github/units/{unitId}/config",
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
      await fetchClient.GET("/api/v1/units/{id}/secrets", {
        params: { path: { id: unitId } },
      }),
    ),
  createUnitSecret: async (unitId: string, body: CreateSecretRequest) =>
    unwrap(
      await fetchClient.POST("/api/v1/units/{id}/secrets", {
        params: { path: { id: unitId } },
        body,
      }),
    ),
  deleteUnitSecret: async (unitId: string, name: string): Promise<void> => {
    assertOk(
      await fetchClient.DELETE("/api/v1/units/{id}/secrets/{name}", {
        params: { path: { id: unitId, name } },
      }),
    );
  },

  // Ollama model discovery (#350) — uses a manual fetch because the
  // endpoint is new and may not be present in the generated schema yet.
  listOllamaModels: async (): Promise<
    { name: string; size: number; modifiedAt: string | null }[]
  > => {
    const resp = await fetch(`${BASE}/api/v1/ollama/models`);
    if (!resp.ok) {
      throw new ApiError(resp.status, resp.statusText, await resp.text());
    }
    return resp.json() as Promise<
      { name: string; size: number; modifiedAt: string | null }[]
    >;
  },
};
