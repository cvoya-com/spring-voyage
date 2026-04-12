import type {
  AgentDashboardSummary,
  AgentDetailResponse,
  ActivityQueryResult,
  CloneResponse,
  CostDashboardSummary,
  CostSummaryResponse,
  InitiativeLevelResponse,
  InitiativePolicy,
  UnitDashboardSummary,
  UnitDetailResponse,
} from "./types";

const BASE = process.env.NEXT_PUBLIC_API_URL ?? "";

async function fetchJSON<T>(path: string, init?: RequestInit): Promise<T> {
  const res = await fetch(`${BASE}${path}`, init);
  if (!res.ok) {
    throw new Error(`API error ${res.status}: ${res.statusText}`);
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
  getUnit: (id: string) =>
    fetchJSON<UnitDetailResponse>(`/api/v1/units/${encodeURIComponent(id)}`),
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
