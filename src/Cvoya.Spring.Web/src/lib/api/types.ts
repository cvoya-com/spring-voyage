// This file is the authoring surface for `@/lib/api/types`. Every type
// that appears in the OpenAPI contract (`./schema.d.ts`, generated from
// `src/Cvoya.Spring.Host.Api/openapi.json`) is re-exported below. The
// remaining hand-written types describe payloads that the contract does
// not cover — notably the SSE activity stream and two string literal
// unions the server models as plain strings.
//
// Import sites stay stable — consumers keep `import type { X } from
// "@/lib/api/types"`. Whether X is hand-written or a re-export is an
// implementation detail of this module.
//
// One generator quirk worth knowing when consuming these types:
// `decimal` fields on the server surface as `string | number` in the
// generated schema (see #181). In practice the server always emits
// numbers, so callers cast with `Number(...)` where arithmetic is
// needed. The contract fix is tracked in #181; until then, the cast
// sites are marked `Number(x) // decimal -> number (#181)`.

import type { components } from "./schema";

type Schemas = components["schemas"];

// ---------------------------------------------------------------------------
// Re-exports from the OpenAPI contract
// ---------------------------------------------------------------------------

/** Address in the v2 domain — e.g., agent://my-agent, unit://my-unit. */
export type Address = Schemas["AddressDto"];

/** Matches Cvoya.Spring.Core.Agents.AgentExecutionMode enum. */
export type AgentExecutionMode = Schemas["AgentExecutionMode"];

/** GET /api/v1/dashboard/agents response item. */
export type AgentDashboardSummary = Schemas["AgentDashboardSummary"];

/** GET /api/v1/dashboard/units response item. */
export type UnitDashboardSummary = Schemas["UnitDashboardSummary"];

/** Cost broken down by source (part of CostDashboardSummary). */
export type CostBySource = Schemas["CostBySource"];

/** GET /api/v1/dashboard/costs response. */
export type CostDashboardSummary = Schemas["CostDashboardSummary"];

/** GET /api/v1/agents/{id} response envelope. */
export type AgentResponse = Schemas["AgentResponse"];

/** GET /api/v1/agents/{id} full response with status. */
export type AgentDetailResponse = Schemas["AgentDetailResponse"];

/** PATCH /api/v1/agents/{id} request body. */
export type UpdateAgentMetadataRequest = Schemas["UpdateAgentMetadataRequest"];

/** Entry in the platform-wide skill catalog (GET /api/v1/skills). */
export type SkillCatalogEntry = Schemas["SkillCatalogEntry"];

/** GET /api/v1/agents/{id}/skills response body. */
export type AgentSkillsResponse = Schemas["AgentSkillsResponse"];

/** Matches Cvoya.Spring.Core.Units.UnitStatus enum. */
export type UnitStatus = Schemas["UnitStatus"];

/** GET /api/v1/units/{id} response envelope. */
export type UnitResponse = Schemas["UnitResponse"];

/** GET /api/v1/units/{id} full response with details. */
export type UnitDetailResponse = Schemas["UnitDetailResponse"];

/** Response body from /api/v1/units/from-yaml and /from-template. */
export type UnitCreationResponse = Schemas["UnitCreationResponse"];

/** POST /api/v1/units/from-yaml request body. */
export type CreateUnitFromYamlRequest = Schemas["CreateUnitFromYamlRequest"];

/** POST /api/v1/units/from-template request body. */
export type CreateUnitFromTemplateRequest =
  Schemas["CreateUnitFromTemplateRequest"];

/** Entry returned by GET /api/v1/packages/templates. */
export type UnitTemplateSummary = Schemas["UnitTemplateSummary"];

/** GET /api/v1/costs/agents/{id} or /units/{id} response. */
export type CostSummaryResponse = Schemas["CostSummaryResponse"];

/** GET /api/v1/agents/{agentId}/clones response item. */
export type CloneResponse = Schemas["CloneResponse"];

/** POST /api/v1/agents/{agentId}/clones request body. */
export type CreateCloneRequest = Schemas["CreateCloneRequest"];

/** GET / PUT budget response. */
export type BudgetResponse = Schemas["BudgetResponse"];

/** PUT budget request body. */
export type SetBudgetRequest = Schemas["SetBudgetRequest"];

/** GET /api/v1/activity query response. */
export type ActivityQueryResult = Schemas["ActivityQueryResult"];

/** Matches Cvoya.Spring.Core.Initiative.InitiativeLevel enum. */
export type InitiativeLevel = Schemas["InitiativeLevel"];

/** Tier 1 (screening) model configuration. */
export type Tier1Config = Schemas["Tier1Config"];

/** Tier 2 (primary LLM) budget/rate-limit configuration. */
export type Tier2Config = Schemas["Tier2Config"];

/** Matches Cvoya.Spring.Core.Initiative.InitiativePolicy record. */
export type InitiativePolicy = Schemas["InitiativePolicy"];

/** GET /api/v1/agents/{id}/initiative/level response. */
export type InitiativeLevelResponse = Schemas["InitiativeLevelResponse"];

// ---------------------------------------------------------------------------
// Hand-written — not surfaced via the HTTP OpenAPI contract
// ---------------------------------------------------------------------------

/**
 * Activity event payload. Delivered via the SSE stream at
 * /api/v1/activity/stream, not a JSON body in the contract; the
 * text/event-stream wire format is not describable in OpenAPI.
 */
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

/** Severity ladder for an activity event; SSE-only. */
export type ActivitySeverity = "Debug" | "Info" | "Warning" | "Error";

/** Activity event shape (SSE payload). */
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

/**
 * Clone lifecycle literals used in CreateCloneRequest. These are string
 * unions on the client; the server models them as plain strings on
 * CreateCloneRequest so the OpenAPI contract has no enum to re-export.
 */
export type CloneType = "ephemeral-no-memory" | "ephemeral-with-memory";

/** Clone attachment mode relative to its parent. See CloneType for why hand-written. */
export type CloneAttachmentMode = "attached" | "detached";
