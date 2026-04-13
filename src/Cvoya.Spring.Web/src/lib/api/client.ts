import createClient from "openapi-fetch";

import type { paths } from "./schema";
import type {
  AgentDetailResponse,
  CreateCloneRequest,
  CreateUnitFromTemplateRequest,
  CreateUnitFromYamlRequest,
  InitiativePolicy,
  SetBudgetRequest,
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
};
