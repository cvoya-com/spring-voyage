// This file is the authoring surface for `@/lib/api/types`. Every type
// that appears in the OpenAPI contract (`./schema.d.ts`, generated from
// `src/Cvoya.Spring.Host.Api/openapi.json`) is re-exported below. The
// remaining hand-written types describe the SSE activity stream — the
// only payload shape OpenAPI cannot describe.
//
// Import sites stay stable — consumers keep `import type { X } from
// "@/lib/api/types"`. Whether X is hand-written or a re-export is an
// implementation detail of this module.

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

/** Response body for GET /api/v1/packages/{package}/templates/{name}. */
export type UnitTemplateDetail = Schemas["UnitTemplateDetail"];

/** Entry in the package browse list (GET /api/v1/packages). */
export type PackageSummary = Schemas["PackageSummary"];

/** Full package detail (GET /api/v1/packages/{name}). */
export type PackageDetail = Schemas["PackageDetail"];

/** Agent template entry inside a package detail. */
export type AgentTemplateSummary = Schemas["AgentTemplateSummary"];

/** Skill bundle entry inside a package detail. */
export type SkillSummary = Schemas["SkillSummary"];

/** Connector asset entry inside a package detail. */
export type PackageConnectorSummary = Schemas["ConnectorSummary"];

/** Workflow bundle entry inside a package detail. */
export type WorkflowSummary = Schemas["WorkflowSummary"];

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

/** Matches Cvoya.Spring.Core.Secrets.SecretScope enum. */
export type SecretScope = Schemas["SecretScope"];

/** Entry in the unit-scoped secrets list. */
export type SecretMetadata = Schemas["SecretMetadata"];

/** GET /api/v1/units/{id}/secrets response body. */
export type UnitSecretsListResponse = Schemas["UnitSecretsListResponse"];

/** POST /api/v1/units/{id}/secrets request body. */
export type CreateSecretRequest = Schemas["CreateSecretRequest"];

/** POST /api/v1/units/{id}/secrets response body. */
export type CreateSecretResponse = Schemas["CreateSecretResponse"];

/** Per-unit/per-agent membership record (GET/PUT /api/v1/units/{unitId}/memberships/{agentAddress}). */
export type UnitMembershipResponse = Schemas["UnitMembershipResponse"];

/** Request body for PUT /api/v1/units/{unitId}/memberships/{agentAddress}. */
export type UpsertMembershipRequest = Schemas["UpsertMembershipRequest"];

// ---------------------------------------------------------------------------
// Connectors (generic + GitHub)
// ---------------------------------------------------------------------------

/** GET /api/v1/connectors response item — uniform, non-polymorphic. */
export type ConnectorTypeResponse = Schemas["ConnectorTypeResponse"];

/** GET /api/v1/units/{id}/connector response — a pointer to the typed config. */
export type UnitConnectorPointerResponse = Schemas["UnitConnectorPointerResponse"];

/**
 * Optional connector binding bundled into a unit-creation request (#199).
 * Allows the wizard to create the unit AND bind a connector in one
 * transactional call. Either `typeId` or `typeSlug` identifies the target
 * connector — the server accepts both and prefers `typeId` when present.
 * `config` is the connector-specific payload (e.g. `UnitGitHubConfigRequest`
 * for the GitHub connector).
 */
export type UnitConnectorBindingRequest =
  Schemas["UnitConnectorBindingRequest"];

/** PUT /api/v1/connectors/github/units/{unitId}/config request body. */
export type UnitGitHubConfigRequest = Schemas["UnitGitHubConfigRequest"];

/** GET / PUT response body for the GitHub per-unit config. */
export type UnitGitHubConfigResponse = Schemas["UnitGitHubConfigResponse"];

/** GET /api/v1/connectors/github/actions/list-installations item. */
export type GitHubInstallationResponse = Schemas["GitHubInstallationResponse"];

/** GET /api/v1/connectors/github/actions/install-url response. */
export type GitHubInstallUrlResponse = Schemas["GitHubInstallUrlResponse"];

/** GET /api/v1/units/{id}/readiness response. */
export type UnitReadinessResponse = Schemas["UnitReadinessResponse"];

// ---------------------------------------------------------------------------
// Dashboard summary (hand-written until next OpenAPI regeneration)
// ---------------------------------------------------------------------------

/** Inline unit detail returned inside the dashboard summary. */
export interface DashboardUnit {
  name: string;
  displayName: string;
  registeredAt: string;
  status: string;
}

/** Inline agent detail returned inside the dashboard summary. */
export interface DashboardAgent {
  name: string;
  displayName: string;
  role: string | null;
  registeredAt: string;
}

/** A single recent-activity item inside the dashboard summary. */
export interface DashboardActivityItem {
  id: string;
  source: string;
  eventType: string;
  severity: string;
  summary: string;
  correlationId?: string;
  cost?: number;
  timestamp: string;
}

/** GET /api/v1/dashboard/summary response. */
export interface DashboardSummary {
  unitCount: number;
  unitsByStatus: Record<string, number>;
  agentCount: number;
  recentActivity: DashboardActivityItem[];
  totalCost: number;
  units: DashboardUnit[];
  agents: DashboardAgent[];
}

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
 * Clone lifecycle type. Re-export of the server's CloningPolicy enum
 * under the legacy name `CloneType` used throughout the web code (#183).
 * Schema defines `"none"` too but the web UI only exposes the ephemeral
 * variants — narrow the type here so call sites can't accidentally
 * widen.
 */
export type CloneType = Exclude<Schemas["CloningPolicy"], "none">;

/** Clone attachment mode relative to its parent. */
export type CloneAttachmentMode = Schemas["AttachmentMode"];
