import type {
  AgentDashboardSummary,
  AgentDetailResponse,
  ActivityQueryResult,
  BudgetResponse,
  CloneResponse,
  CostDashboardSummary,
  CostSummaryResponse,
  CreateCloneRequest,
  InitiativeLevelResponse,
  InitiativePolicy,
  SetBudgetRequest,
  UnitDashboardSummary,
  UnitDetailResponse,
  UnitResponse,
  UnitStatus,
} from "./types";

const BASE = process.env.NEXT_PUBLIC_API_URL ?? "";

async function fetchJSON<T>(path: string, init?: RequestInit): Promise<T> {
  const res = await fetch(`${BASE}${path}`, init);
  if (!res.ok) {
    const text = await res.text().catch(() => "");
    throw new Error(
      `API error ${res.status}: ${res.statusText}${text ? ` — ${text}` : ""}`,
    );
  }
  return res.json() as Promise<T>;
}

async function postJSON<T>(path: string, body: unknown): Promise<T> {
  return fetchJSON<T>(path, {
    method: "POST",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify(body),
  });
}

async function postJSONNoBody<T>(path: string): Promise<T> {
  const res = await fetch(`${BASE}${path}`, { method: "POST" });
  if (!res.ok) {
    const text = await res.text().catch(() => "");
    throw new Error(
      `API error ${res.status}: ${res.statusText}${text ? ` — ${text}` : ""}`,
    );
  }
  return res.json() as Promise<T>;
}

async function putJSON(path: string, body: unknown): Promise<void> {
  const res = await fetch(`${BASE}${path}`, {
    method: "PUT",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify(body),
  });
  if (!res.ok) {
    throw new Error(`API error ${res.status}: ${res.statusText}`);
  }
}

async function putJSONReturn<T>(path: string, body: unknown): Promise<T> {
  const res = await fetch(`${BASE}${path}`, {
    method: "PUT",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify(body),
  });
  if (!res.ok) {
    const text = await res.text().catch(() => "");
    throw new Error(
      `API error ${res.status}: ${res.statusText}${text ? ` — ${text}` : ""}`,
    );
  }
  return res.json() as Promise<T>;
}

async function patchJSON<T>(path: string, body: unknown): Promise<T> {
  const res = await fetch(`${BASE}${path}`, {
    method: "PATCH",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify(body),
  });
  if (!res.ok) {
    const text = await res.text().catch(() => "");
    throw new Error(
      `API error ${res.status}: ${res.statusText}${text ? ` — ${text}` : ""}`,
    );
  }
  return res.json() as Promise<T>;
}

async function deleteJSON(path: string): Promise<void> {
  const res = await fetch(`${BASE}${path}`, { method: "DELETE" });
  if (!res.ok) {
    throw new Error(`API error ${res.status}: ${res.statusText}`);
  }
}

export const api = {
  // Dashboard
  getDashboardAgents: () =>
    fetchJSON<AgentDashboardSummary[]>("/api/v1/dashboard/agents"),
  getDashboardUnits: () =>
    fetchJSON<UnitDashboardSummary[]>("/api/v1/dashboard/units"),
  getDashboardCosts: () =>
    fetchJSON<CostDashboardSummary>("/api/v1/dashboard/costs"),

  // Agents
  getAgent: (id: string) =>
    fetchJSON<AgentDetailResponse>(`/api/v1/agents/${encodeURIComponent(id)}`),
  deleteAgent: (id: string) =>
    deleteJSON(`/api/v1/agents/${encodeURIComponent(id)}`),

  // Units
  // Detailed unit read — includes Members and raw status payload. Used by the
  // legacy query-string detail view under /units?id=... and still useful for
  // anything that needs the members/details blob.
  getUnitDetail: (id: string) =>
    fetchJSON<UnitDetailResponse>(`/api/v1/units/${encodeURIComponent(id)}`),
  // Lightweight unit read that returns the unit envelope only. Used by the
  // /units/[id] config page where the tabs shell pulls data independently.
  getUnit: async (id: string): Promise<UnitResponse> => {
    const detail = await fetchJSON<UnitDetailResponse>(
      `/api/v1/units/${encodeURIComponent(id)}`,
    );
    return detail.unit;
  },
  createUnit: (body: {
    name: string;
    displayName: string;
    description: string;
    model?: string;
    color?: string;
  }) => postJSON<UnitResponse>("/api/v1/units", body),
  updateUnit: (
    id: string,
    patch: Partial<{
      displayName: string;
      description: string;
      model: string;
      color: string;
    }>,
  ) =>
    patchJSON<UnitResponse>(
      `/api/v1/units/${encodeURIComponent(id)}`,
      patch,
    ),
  startUnit: (id: string) =>
    postJSONNoBody<{ unitId: string; status: UnitStatus }>(
      `/api/v1/units/${encodeURIComponent(id)}/start`,
    ),
  stopUnit: (id: string) =>
    postJSONNoBody<{ unitId: string; status: UnitStatus }>(
      `/api/v1/units/${encodeURIComponent(id)}/stop`,
    ),
  deleteUnit: (id: string) =>
    deleteJSON(`/api/v1/units/${encodeURIComponent(id)}`),
  addMember: (unitId: string, memberScheme: string, memberPath: string) =>
    postJSON(`/api/v1/units/${encodeURIComponent(unitId)}/members`, {
      memberAddress: { scheme: memberScheme, path: memberPath },
    }),
  removeMember: (unitId: string, memberId: string) =>
    deleteJSON(`/api/v1/units/${encodeURIComponent(unitId)}/members/${encodeURIComponent(memberId)}`),

  // Costs
  getAgentCost: (id: string) =>
    fetchJSON<CostSummaryResponse>(`/api/v1/costs/agents/${encodeURIComponent(id)}`),
  getUnitCost: (id: string) =>
    fetchJSON<CostSummaryResponse>(`/api/v1/costs/units/${encodeURIComponent(id)}`),

  // Clones
  getClones: (agentId: string) =>
    fetchJSON<CloneResponse[]>(`/api/v1/agents/${encodeURIComponent(agentId)}/clones`),
  createClone: (agentId: string, body: CreateCloneRequest) =>
    postJSON<CloneResponse>(
      `/api/v1/agents/${encodeURIComponent(agentId)}/clones`,
      body,
    ),
  deleteClone: (agentId: string, cloneId: string) =>
    deleteJSON(
      `/api/v1/agents/${encodeURIComponent(agentId)}/clones/${encodeURIComponent(cloneId)}`,
    ),

  // Budgets
  getAgentBudget: (agentId: string) =>
    fetchJSON<BudgetResponse>(
      `/api/v1/agents/${encodeURIComponent(agentId)}/budget`,
    ),
  setAgentBudget: (agentId: string, body: SetBudgetRequest) =>
    putJSONReturn<BudgetResponse>(
      `/api/v1/agents/${encodeURIComponent(agentId)}/budget`,
      body,
    ),
  getTenantBudget: () => fetchJSON<BudgetResponse>("/api/v1/tenant/budget"),
  setTenantBudget: (body: SetBudgetRequest) =>
    putJSONReturn<BudgetResponse>("/api/v1/tenant/budget", body),

  // Activity
  queryActivity: (params?: Record<string, string>) => {
    const qs = params ? `?${new URLSearchParams(params)}` : "";
    return fetchJSON<ActivityQueryResult>(`/api/v1/activity${qs}`);
  },

  // Initiative
  getAgentInitiativePolicy: (id: string) =>
    fetchJSON<InitiativePolicy>(
      `/api/v1/agents/${encodeURIComponent(id)}/initiative/policy`,
    ),
  setAgentInitiativePolicy: (id: string, policy: InitiativePolicy) =>
    putJSON(
      `/api/v1/agents/${encodeURIComponent(id)}/initiative/policy`,
      policy,
    ),
  getAgentInitiativeLevel: (id: string) =>
    fetchJSON<InitiativeLevelResponse>(
      `/api/v1/agents/${encodeURIComponent(id)}/initiative/level`,
    ),
  getUnitInitiativePolicy: (id: string) =>
    fetchJSON<InitiativePolicy>(
      `/api/v1/units/${encodeURIComponent(id)}/initiative/policy`,
    ),
  setUnitInitiativePolicy: (id: string, policy: InitiativePolicy) =>
    putJSON(
      `/api/v1/units/${encodeURIComponent(id)}/initiative/policy`,
      policy,
    ),
};
