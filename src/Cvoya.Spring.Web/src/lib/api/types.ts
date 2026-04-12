/** Address in the v2 domain — e.g., agent://my-agent, unit://my-unit. */
export interface Address {
  scheme: string;
  path: string;
}

/** Matches Cvoya.Spring.Core.Capabilities.ActivityEventType enum. */
export type ActivityEventType =
  | "MessageReceived"
  | "MessageSent"
  | "ConversationStarted"
  | "ConversationCompleted"
  | "DecisionMade"
  | "ErrorOccurred"
  | "StateChanged"
  | "InitiativeTriggered"
  | "ReflectionCompleted"
  | "WorkflowStepCompleted"
  | "CostIncurred"
  | "TokenDelta";

/** Matches Cvoya.Spring.Core.Capabilities.ActivitySeverity enum. */
export type ActivitySeverity = "Debug" | "Info" | "Warning" | "Error";

/** Matches Cvoya.Spring.Core.Capabilities.ActivityEvent record (SSE payload). */
export interface ActivityEvent {
  id: string;
  timestamp: string;
  source: Address;
  eventType: ActivityEventType;
  severity: ActivitySeverity;
  summary: string;
  details?: unknown;
  correlationId?: string;
  cost?: number;
}

/** GET /api/v1/dashboard/agents response item. */
export interface AgentDashboardSummary {
  name: string;
  displayName: string;
  role?: string;
  registeredAt: string;
}

/** GET /api/v1/dashboard/units response item. */
export interface UnitDashboardSummary {
  name: string;
  displayName: string;
  registeredAt: string;
}

/** Cost broken down by source (part of CostDashboardSummary). */
export interface CostBySource {
  source: string;
  totalCost: number;
}

/** GET /api/v1/dashboard/costs response. */
export interface CostDashboardSummary {
  totalCost: number;
  costsBySource: CostBySource[];
  periodStart?: string;
  periodEnd?: string;
}

/** GET /api/v1/agents/{id} response. */
export interface AgentResponse {
  id: string;
  name: string;
  displayName: string;
  description: string;
  role?: string;
  registeredAt: string;
}

/** GET /api/v1/agents/{id} full response with status. */
export interface AgentDetailResponse {
  agent: AgentResponse;
  status?: unknown;
}

/** Matches Cvoya.Spring.Core.Units.UnitStatus enum. */
export type UnitStatus =
  | "Draft"
  | "Stopped"
  | "Starting"
  | "Running"
  | "Stopping"
  | "Error";

/** GET /api/v1/units/{id} response. */
export interface UnitResponse {
  id: string;
  name: string;
  displayName: string;
  description: string;
  registeredAt: string;
  status?: UnitStatus | number | string;
  model?: string | null;
  color?: string | null;
}

/** GET /api/v1/units/{id} full response with details. */
export interface UnitDetailResponse {
  unit: UnitResponse;
  details?: unknown;
}

/** GET /api/v1/costs/agents/{id} or /units/{id} response. */
export interface CostSummaryResponse {
  totalCost: number;
  totalInputTokens: number;
  totalOutputTokens: number;
  recordCount: number;
  from: string;
  to: string;
}

/** Clone lifecycle type. Matches values accepted by CreateCloneRequest. */
export type CloneType = "ephemeral-no-memory" | "ephemeral-with-memory";

/** Clone attachment mode relative to its parent. */
export type CloneAttachmentMode = "attached" | "detached";

/** GET /api/v1/agents/{agentId}/clones response item. */
export interface CloneResponse {
  cloneId: string;
  parentAgentId: string;
  cloneType: CloneType;
  attachmentMode: CloneAttachmentMode;
  status: string;
  createdAt: string;
}

/** POST /api/v1/agents/{agentId}/clones request body. */
export interface CreateCloneRequest {
  cloneType: CloneType;
  attachmentMode: CloneAttachmentMode;
}

/** GET/PUT budget response. */
export interface BudgetResponse {
  dailyBudget: number;
}

/** PUT budget request body. */
export interface SetBudgetRequest {
  dailyBudget: number;
}

/** GET /api/v1/activity query response. */
export interface ActivityQueryResult {
  items: ActivityEvent[];
  totalCount: number;
  page: number;
  pageSize: number;
}

/** Matches Cvoya.Spring.Core.Initiative.InitiativeLevel enum. */
export type InitiativeLevel =
  | "Passive"
  | "Attentive"
  | "Proactive"
  | "Autonomous";

/** Tier 1 (screening) model configuration. */
export interface Tier1Config {
  Model: string;
  Hosting: string;
}

/** Tier 2 (primary LLM) budget/rate-limit configuration. */
export interface Tier2Config {
  MaxCallsPerHour: number;
  MaxCostPerDay: number;
}

/**
 * Matches Cvoya.Spring.Core.Initiative.InitiativePolicy record.
 * Field names are PascalCase because the C# record is serialised as-is.
 */
export interface InitiativePolicy {
  MaxLevel: InitiativeLevel;
  RequireUnitApproval: boolean;
  Tier1: Tier1Config | null;
  Tier2: Tier2Config | null;
  AllowedActions: string[] | null;
  BlockedActions: string[] | null;
}

/** GET /api/v1/agents/{id}/initiative/level response. */
export interface InitiativeLevelResponse {
  level: InitiativeLevel;
}
