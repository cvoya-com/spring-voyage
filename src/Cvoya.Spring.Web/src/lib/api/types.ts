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

/**
 * Response body for the persistent-agent lifecycle verbs
 * (`POST /deploy|undeploy|scale`, `GET /deployment`). Also embedded
 * inside `AgentDetailResponse.deployment` when the agent has a tracked
 * container deployment in the registry (#396).
 */
export type PersistentAgentDeploymentResponse =
  Schemas["PersistentAgentDeploymentResponse"];

/** Request body for `POST /api/v1/agents/{id}/deploy`. */
export type DeployPersistentAgentRequest =
  Schemas["DeployPersistentAgentRequest"];

/** Request body for `POST /api/v1/agents/{id}/scale`. */
export type ScalePersistentAgentRequest =
  Schemas["ScalePersistentAgentRequest"];

/**
 * Response body for `GET /api/v1/agents/{id}/logs`. Currently a snapshot
 * (server-side `docker logs --tail`); the stream upgrade is tracked as a
 * follow-up.
 */
export type PersistentAgentLogsResponse =
  Schemas["PersistentAgentLogsResponse"];

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

// ---------------------------------------------------------------------------
// Conversations & inbox (#410, #452, #456)
// ---------------------------------------------------------------------------

/** Row in the conversation list (`GET /api/v1/conversations`). */
export type ConversationSummary = Schemas["ConversationSummary"];

/** Conversation thread payload (`GET /api/v1/conversations/{id}`). */
export type ConversationDetail = Schemas["ConversationDetail"];

/** One event on a conversation thread. */
export type ConversationEvent = Schemas["ConversationEvent"];

/** Request body for `POST /api/v1/conversations/{id}/messages`. */
export type ConversationMessageRequest = Schemas["ConversationMessageRequest"];

/** Response body for `POST /api/v1/conversations/{id}/messages`. */
export type ConversationMessageResponse = Schemas["ConversationMessageResponse"];

/** Row in the awaiting-me queue (`GET /api/v1/inbox`). */
export type InboxItem = Schemas["InboxItem"];

/** Query-string filters accepted by `GET /api/v1/conversations`. */
export interface ConversationListFilters {
  unit?: string;
  agent?: string;
  status?: "active" | "completed";
  participant?: string;
  limit?: number;
}

/** Matches Cvoya.Spring.Core.Initiative.InitiativeLevel enum. */
export type InitiativeLevel = Schemas["InitiativeLevel"];

// ---------------------------------------------------------------------------
// Unit policy — governance across five dimensions (#411, #462 PR-R5)
// ---------------------------------------------------------------------------

/**
 * Skill (tool) allow/block list. Empty `allowed` means "allow all".
 * Blocked entries always deny. Mirrors the CLI's
 * `spring unit policy skill` dimension (#453).
 */
export type SkillPolicy = Schemas["SkillPolicy"];

/** LLM model allow/block list. Same shape as {@link SkillPolicy}. */
export type ModelPolicy = Schemas["ModelPolicy"];

/**
 * Per-invocation / per-hour / per-day cost caps (USD). A null value
 * means "no cap on that window"; the server treats absent caps as
 * "inherit from parent" once #414 lands.
 */
export type CostPolicy = Schemas["CostPolicy"];

/**
 * Execution-mode constraint. `forced` pins every member to one mode;
 * `allowed` is a whitelist the member must fall within. Setting both
 * is legal — `forced` is a stronger statement than `allowed`.
 */
export type ExecutionModePolicy = Schemas["ExecutionModePolicy"];

/**
 * Unified unit policy — the wire shape returned by
 * `GET /api/v1/units/{id}/policy` and accepted by `PUT` (with a `null`
 * body meaning "clear all dimensions"). Every dimension is optional;
 * absent dimensions are unconstrained on this unit.
 */
export type UnitPolicyResponse = Schemas["UnitPolicyResponse"];

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
 * GET /api/v1/connectors/{slugOrId}/bindings response item (#520). One entry
 * per unit currently bound to the requested connector type. Collapses the
 * per-unit fan-out that `useConnectorBindings` used to issue into a single
 * round-trip.
 */
export type ConnectorUnitBindingResponse = Schemas["ConnectorUnitBindingResponse"];

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
// Unit boundary (#413 — opacity / projection / synthesis)
// ---------------------------------------------------------------------------

/** GET / PUT response body for `/api/v1/units/{id}/boundary`. */
export type UnitBoundaryResponse = Schemas["UnitBoundaryResponse"];

/** One opacity rule — matched entries are hidden from outside callers. */
export type BoundaryOpacityRuleDto = Schemas["BoundaryOpacityRuleDto"];

/** One projection rule — matched entries are rewritten for outside callers. */
export type BoundaryProjectionRuleDto = Schemas["BoundaryProjectionRuleDto"];

/** One synthesis rule — matched entries collapse into a single unit-level entry. */
export type BoundarySynthesisRuleDto = Schemas["BoundarySynthesisRuleDto"];

// ---------------------------------------------------------------------------
// Expertise directory (#412, #486, #488)
// ---------------------------------------------------------------------------

/** A single expertise domain on an agent or unit. */
export type ExpertiseDomainDto = Schemas["ExpertiseDomainDto"];

/** GET/PUT response for agent and unit "own" expertise. */
export type ExpertiseResponse = Schemas["ExpertiseResponse"];

/** PUT request body for agent/unit own expertise. */
export type SetExpertiseRequest = Schemas["SetExpertiseRequest"];

/** One entry in the unit's recursively-aggregated expertise list. */
export type AggregatedExpertiseEntryDto =
  Schemas["AggregatedExpertiseEntryDto"];

/** GET /api/v1/units/{id}/expertise response body. */
export type AggregatedExpertiseResponse = Schemas["AggregatedExpertiseResponse"];

/** POST /api/v1/directory/search request body (#542). */
export type DirectorySearchRequest = Schemas["DirectorySearchRequest"];

/** One hit in a POST /api/v1/directory/search response (#542). */
export type DirectorySearchHitResponse = Schemas["DirectorySearchHitResponse"];

/** POST /api/v1/directory/search response body (#542). */
export type DirectorySearchResponse = Schemas["DirectorySearchResponse"];

/**
 * Whitelist of expertise levels accepted by the server (see
 * `ExpertiseCommand.ParseDomainSpec` in the CLI). A `null` level is allowed
 * and means "level unspecified"; the UI renders the level chip as blank.
 */
export const EXPERTISE_LEVELS = [
  "beginner",
  "intermediate",
  "advanced",
  "expert",
] as const;
export type ExpertiseLevel = (typeof EXPERTISE_LEVELS)[number];

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
