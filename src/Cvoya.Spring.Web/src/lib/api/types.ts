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

/**
 * POST /api/v1/agents request body. Mirrors the `spring agent create`
 * CLI surface 1:1: the wire shape requires `name`, `displayName`,
 * `description`, `unitIds` (≥1), and an optional `role` /
 * `definitionJson`. The portal `/agents/create` page and the unit
 * Agents-tab inline-create dialog both POST through this shape via the
 * shared `createAgentFromForm` helper in `@/lib/agents/create-agent`.
 */
export type CreateAgentRequest = Schemas["CreateAgentRequest"];

// ---------------------------------------------------------------------------
// Platform metadata + Auth surface — consumed by the Settings drawer (#451).
// ---------------------------------------------------------------------------

/** GET /api/v1/platform/info response — version / build hash / license. */
export type PlatformInfoResponse = Schemas["PlatformInfoResponse"];

/** GET /api/v1/auth/me response — current authenticated user. */
export type UserProfileResponse = Schemas["UserProfileResponse"];

/** One item in the GET /api/v1/auth/tokens response. */
export type TokenResponse = Schemas["TokenResponse"];

/** POST /api/v1/auth/tokens request body — mirrors `spring auth token create <name>`. */
export type CreateTokenRequest = Schemas["CreateTokenRequest"];

/**
 * POST /api/v1/auth/tokens response — the newly-created token.
 * The `token` field is the plaintext secret; it is returned ONCE and never
 * again. The caller must display it to the operator and scrub it from state
 * on dismiss (one-shot reveal pattern, #557).
 */
export type CreateTokenResponse = Schemas["CreateTokenResponse"];

/** Entry in the platform-wide skill catalog (GET /api/v1/skills). */
export type SkillCatalogEntry = Schemas["SkillCatalogEntry"];

/** GET /api/v1/agents/{id}/skills response body. */
export type AgentSkillsResponse = Schemas["AgentSkillsResponse"];

/** Matches Cvoya.Spring.Core.Units.UnitStatus enum. */
export type UnitStatus = Schemas["UnitStatus"];

/**
 * Structured error surfaced on `UnitResponse.lastValidationError` after the
 * backend UnitValidationWorkflow ends in `Error`. Carries the step that
 * failed (`PullingImage` / `VerifyingTool` / `ValidatingCredential` /
 * `ResolvingModel`), a stable code from `UnitValidationCodes`, an
 * operator-facing message, and optional details. The T-07 Validation
 * panel branches on `code` to render friendly remediation copy.
 */
export type UnitValidationError = Schemas["UnitValidationError"];

/**
 * One of four probe steps emitted by the backend UnitValidationWorkflow
 * (T-04). Order mirrors the workflow: `PullingImage → VerifyingTool →
 * ValidatingCredential → ResolvingModel`.
 */
export type UnitValidationStep = Schemas["UnitValidationStep"];

/** GET /api/v1/units/{id} response envelope. */
export type UnitResponse = Schemas["UnitResponse"];

/** GET /api/v1/units/{id} full response with details. */
export type UnitDetailResponse = Schemas["UnitDetailResponse"];

/** Response body for GET /api/v1/packages/{package}/templates/{name}. */
export type UnitTemplateDetail = Schemas["UnitTemplateDetail"];

/** Entry in the package browse list (GET /api/v1/packages). */
export type PackageSummary = Schemas["PackageSummary"];

/** Full package detail (GET /api/v1/packages/{name}). */
export type PackageDetail = Schemas["PackageDetail"];

/** GET /api/v1/costs/agents/{id} or /units/{id} response. */
export type CostSummaryResponse = Schemas["CostSummaryResponse"];

/**
 * GET /api/v1/tenant/cost/timeseries response (V21-tenant-cost-timeseries,
 * #916). Zero-filled tenant cost series bucketed by fixed UTC intervals.
 * Shared between the `/budgets` sparkline and the forthcoming analytics
 * time-series chart (#910).
 */
export type TenantCostTimeseriesResponse = Schemas["CostTimeseriesResponse"];

/**
 * GET /api/v1/tenant/analytics/agents/{id}/cost-timeseries and
 * GET /api/v1/tenant/analytics/units/{id}/cost-timeseries response (#1363).
 * Zero-filled agent/unit cost series bucketed by fixed UTC intervals.
 */
export type AnalyticsCostTimeseriesResponse =
  Schemas["AnalyticsCostTimeseriesResponse"];

/**
 * GET /api/v1/tenant/cost/agents/{id}/breakdown response (#1364).
 * Per-model cost breakdown for a single agent, descending by cost.
 *
 * Per-bucket / per-entry shapes are accessible via array indexing
 * (`AnalyticsCostTimeseriesResponse["points"][number]`,
 * `CostBreakdownResponse["entries"][number]`); not re-exported standalone
 * to keep the surface narrow (knip).
 */
export type CostBreakdownResponse = Schemas["CostBreakdownResponse"];

/** GET /api/v1/agents/{agentId}/clones response item. */
export type CloneResponse = Schemas["CloneResponse"];

/** POST /api/v1/agents/{agentId}/clones request body. */
export type CreateCloneRequest = Schemas["CreateCloneRequest"];

/**
 * GET/PUT response body for the persistent cloning policy (PR-PLAT-CLONE-1,
 * #416). Surfaced at per-agent (`/agents/{id}/cloning-policy`) and
 * tenant-wide (`/tenant/cloning-policy`) scopes. An absent field means
 * "no constraint on that axis"; the server always returns a valid object
 * (never 404) even if no policy has been persisted.
 */
export type AgentCloningPolicyResponse = Schemas["AgentCloningPolicyResponse"];

/**
 * Per-field nested types (`CloningPolicy` enum, `AttachmentMode` enum) are accessible
 * via `AgentCloningPolicyResponse["allowedPolicies"][number]` /
 * `AgentCloningPolicyResponse["allowedAttachmentModes"][number]` indexing when needed.
 * Not re-exported standalone to keep the surface narrow (knip).
 */

/** GET / PUT budget response. */
export type BudgetResponse = Schemas["BudgetResponse"];

/** PUT budget request body. */
export type SetBudgetRequest = Schemas["SetBudgetRequest"];

/** GET /api/v1/activity query response. */
export type ActivityQueryResult = Schemas["ActivityQueryResult"];

// ---------------------------------------------------------------------------
// Analytics (#448, #457) — Throughput / Wait-time rollups
// ---------------------------------------------------------------------------
//
// Every shape below mirrors the `spring analytics {throughput,waits}` CLI
// surface (PR #474) 1:1 so UI and CLI can never drift on the wire contract.
// Costs round-trip the existing `CostSummaryResponse` and
// `CostDashboardSummary` shapes.

/** One row in `GET /api/v1/analytics/throughput`. */
export type ThroughputEntryResponse = Schemas["ThroughputEntryResponse"];

/** Response body of `GET /api/v1/analytics/throughput`. */
export type ThroughputRollupResponse = Schemas["ThroughputRollupResponse"];

/** One row in `GET /api/v1/analytics/waits`. */
export type WaitTimeEntryResponse = Schemas["WaitTimeEntryResponse"];

/** Response body of `GET /api/v1/analytics/waits`. */
export type WaitTimeRollupResponse = Schemas["WaitTimeRollupResponse"];

/**
 * Window labels shared by the three Analytics pages. Matches the CLI's
 * `--window 24h|7d|30d` surface on `spring analytics costs|throughput|waits`
 * (PR #474). A portal picker that chooses one of these values resolves to
 * the same `(from, to)` pair the CLI would.
 */
export const ANALYTICS_WINDOWS = ["24h", "7d", "30d"] as const;
export type AnalyticsWindow = (typeof ANALYTICS_WINDOWS)[number];

/** The tri-state scope filter exposed by each Analytics page. */
export type AnalyticsScope =
  | { kind: "all" }
  | { kind: "unit"; name: string }
  | { kind: "agent"; name: string };

// ---------------------------------------------------------------------------
// Threads & inbox (#410, #452, #456)
// ---------------------------------------------------------------------------

/** Row in the thread list (`GET /api/v1/threads`). */
export type ThreadSummary = Schemas["ThreadSummaryResponse"];

/** Thread payload (`GET /api/v1/threads/{id}`). */
export type ThreadDetail = Schemas["ThreadDetailResponse"];

/** One event row inside a thread (see ThreadDetail.events). */
export type ThreadEvent = Schemas["ThreadEventResponse"];

/**
 * A participant address with a resolved human-readable display name.
 * Used in `InboxItem.from`, `InboxItem.human`, `ThreadSummary.participants`,
 * `ThreadSummary.origin`, `ThreadEvent.source`, and `ThreadEvent.from`.
 */
export type ParticipantRef = Schemas["ParticipantRef"];

/** Request body for `POST /api/v1/threads/{id}/messages`. */
export type ThreadMessageRequest = Schemas["ThreadMessageRequest"];

/** Request body for `POST /api/v1/messages` — free-form message routing. */
export type SendMessageRequest = Schemas["SendMessageRequest"];

/** Response body for `POST /api/v1/messages`. */
export type MessageResponse = Schemas["MessageResponse"];

/** Row in the awaiting-me queue (`GET /api/v1/inbox`). */
export type InboxItem = Schemas["InboxItemResponse"];

/** Query-string filters accepted by `GET /api/v1/threads`. */
export interface ThreadListFilters {
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

/**
 * Sixth `UnitPolicy` dimension — label-routing rules consumed by the
 * `label-routed` `IOrchestrationStrategy` (#389). Edited alongside the
 * other five dimensions through `PUT /api/v1/units/{id}/policy`.
 */
export type LabelRoutingPolicy = Schemas["LabelRoutingPolicy"];

/**
 * Platform-offered orchestration strategy keys (#491). The resolver
 * looks up the manifest's declared key against this set (with hosts
 * free to register additional keys); the portal surfaces exactly these
 * three in its Orchestration tab dropdown because they are the only
 * ones guaranteed to be registered by the OSS platform.
 */
export const ORCHESTRATION_STRATEGIES = [
  "ai",
  "workflow",
  "label-routed",
] as const;
export type OrchestrationStrategyKey = (typeof ORCHESTRATION_STRATEGIES)[number];

/**
 * Wire shape for `GET/PUT /api/v1/units/{id}/orchestration` (#606). The
 * dedicated surface ADR-0010 deferred — consumed by the Orchestration
 * tab's strategy selector so the dropdown becomes directly editable
 * instead of linking out to `spring apply`.
 */
export type UnitOrchestrationResponse = Schemas["UnitOrchestrationResponse"];

/**
 * Wire shape for `GET/PUT /api/v1/units/{id}/execution` (#601 / #603 /
 * #409 B-wide — backend in PR #628). Holds the unit-level defaults
 * (image / runtime / tool / provider / model) that member agents inherit
 * at dispatch time. Every field is independently nullable: a unit may
 * declare any subset. A PUT replaces the whole persisted block, so the
 * portal always sends the full merged shape; per-field clear issues a
 * PUT with the remaining fields (and a final DELETE when all fields end
 * up null).
 */
export type UnitExecutionResponse = Schemas["UnitExecutionResponse"];

/**
 * Wire shape for `GET/PUT /api/v1/agents/{id}/execution` (#601 / #603 /
 * #409 B-wide — backend in PR #628). Mirrors {@link UnitExecutionResponse}
 * plus the agent-exclusive `hosting` field (`ephemeral` / `persistent`).
 * Response carries only the agent's own declared fields — inherited
 * unit defaults are NOT merged in by the endpoint; the portal's
 * `inherited from unit` indicator overlays them from the owning unit's
 * `/execution` response (read from {@link UnitExecutionResponse}).
 */
export type AgentExecutionResponse = Schemas["AgentExecutionResponse"];

/**
 * Platform-offered runtime keys (#601). The portal surfaces only these
 * two because the reference dispatcher knows how to launch containers
 * through them; custom runtimes are registered on the host as an
 * extension but a generic dropdown can't describe them.
 */
export const EXECUTION_RUNTIMES = ["docker", "podman"] as const;

/**
 * Launcher keys the reference dispatcher ships with (#601). Mirrors the
 * `ExecutionTool` set in `src/lib/ai-models.ts` one-for-one — kept here
 * alongside the execution wire shapes so the unit/agent Execution panels
 * can render the dropdown without pulling in the AI-model catalog.
 */
export const EXECUTION_TOOL_KEYS = [
  "claude-code",
  "codex",
  "gemini",
  "dapr-agent",
  "custom",
] as const;

/**
 * Provider keys accepted by the unit/agent Execution surfaces when the
 * launcher is `dapr-agent`. The backend's canonical mapping now lives on
 * each runtime's <c>IAgentRuntime.CredentialSecretName</c>; the
 * credential-status endpoint (<c>GET /system/credentials/&lcub;provider&rcub;/status</c>)
 * accepts `anthropic`, `openai`, `google`, `ollama` and translates
 * `anthropic` to the Claude runtime id before consulting the registry.
 * The portal dropdown standardises on these canonical names so the
 * value round-trips cleanly.
 */
export const EXECUTION_PROVIDERS = [
  "anthropic",
  "openai",
  "google",
  "ollama",
] as const;

/** Matches Cvoya.Spring.Core.Initiative.InitiativePolicy record. */
export type InitiativePolicy = Schemas["InitiativePolicy"];

/** GET /api/v1/agents/{id}/initiative/level response. */
export type InitiativeLevelResponse = Schemas["InitiativeLevelResponse"];

/** Entry in the unit-scoped secrets list. */
export type SecretMetadata = Schemas["SecretMetadata"];

/** POST /api/v1/units/{id}/secrets request body. */
export type CreateSecretRequest = Schemas["CreateSecretRequest"];

/** Per-unit/per-agent membership record (GET/PUT /api/v1/units/{unitId}/memberships/{agentAddress}). */
export type UnitMembershipResponse = Schemas["UnitMembershipResponse"];

// ---------------------------------------------------------------------------
// Package install (ADR-0035 decision 11 — two-phase atomic install).
// Endpoints: POST /api/v1/packages/install, GET/POST /api/v1/installs/{id}.
// ---------------------------------------------------------------------------

/** One target within a package install request (POST /api/v1/packages/install). */
export type PackageInstallTarget = Schemas["PackageInstallTarget"];

/** Response body shared by POST /api/v1/packages/install, GET /api/v1/installs/{id}, and POST /api/v1/installs/{id}/retry. */
export type InstallStatusResponse = Schemas["InstallStatusResponse"];

/** Per-package detail row within an InstallStatusResponse. */
export type InstallPackageDetail = Schemas["InstallPackageDetail"];

// ---------------------------------------------------------------------------
// Connectors (generic + GitHub)
// ---------------------------------------------------------------------------

/**
 * GET /api/v1/connectors response item — one entry per connector installed
 * on the current tenant (#714). The type-descriptor fields
 * (`typeId`, `typeSlug`, `displayName`, `description`, `configUrl`,
 * `actionsBaseUrl`, `configSchemaUrl`) come from the registered
 * `IConnectorType`; `installedAt`, `updatedAt`, and `config` come from
 * the tenant install row. Pre-#714 the endpoint returned every registered
 * connector type regardless of tenant-install state under the name
 * `ConnectorTypeResponse`; that schema was retired when the list/get
 * semantics pivoted to tenant-install.
 */
export type InstalledConnectorResponse = Schemas["InstalledConnectorResponse"];

// ---------------------------------------------------------------------------
// Agent runtimes (#690)
// ---------------------------------------------------------------------------

/**
 * GET /api/v1/agent-runtimes item — an agent runtime installed on the
 * current tenant (#690). Combines runtime-descriptor fields
 * (`id`, `displayName`, `toolKind`, `credentialKind`,
 * `credentialDisplayHint`) with the tenant install config (`models`,
 * `defaultModel`, `baseUrl`). Feeds the unit-creation wizard's provider
 * + model dropdowns.
 */
export type InstalledAgentRuntimeResponse =
  Schemas["InstalledAgentRuntimeResponse"];

/**
 * One entry in GET /api/v1/agent-runtimes/{id}/models.
 * @public Re-export consumed via the namespaced `types.*` import in
 * `client.ts` / `queries.ts` (knip can't trace namespace re-exports).
 */
export type AgentRuntimeModelResponse = Schemas["AgentRuntimeModelResponse"];

/**
 * Response body for POST /api/v1/agent-runtimes/{id}/validate-credential.
 * @public Re-export consumed via the namespaced `types.*` import in
 * `client.ts` (knip can't trace namespace re-exports).
 */
export type CredentialValidateResponse = Schemas["CredentialValidateResponse"];

/**
 * Persistent credential status for a stored credential on an agent
 * runtime or connector. Returned by the `credential-health` endpoints
 * on both surfaces and surfaced read-only in the portal admin views
 * (#691). A single network error does NOT flip the persistent status.
 */
export type CredentialHealthStatus = Schemas["CredentialHealthStatus"];

/**
 * GET response body for
 * `/api/v1/agent-runtimes/{id}/credential-health` and
 * `/api/v1/connectors/{slugOrId}/credential-health`.
 */
export type CredentialHealthResponse = Schemas["CredentialHealthResponse"];

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

/**
 * GET / PUT response body for the GitHub per-unit config.
 * @public Consumed by `Cvoya.Spring.Connector.GitHub/web/connector-tab.tsx`
 * (cross-workspace; knip's path-alias resolver doesn't follow `@/*`
 * from outside this workspace).
 */
export type UnitGitHubConfigResponse = Schemas["UnitGitHubConfigResponse"];

/**
 * GET /api/v1/connectors/github/actions/list-installations item.
 * @public Consumed by `Cvoya.Spring.Connector.GitHub/web/*` cross-workspace.
 */
export type GitHubInstallationResponse = Schemas["GitHubInstallationResponse"];

/**
 * GET /api/v1/connectors/github/actions/list-repositories item (#1133).
 * One row per repo the App can see, aggregated across every visible
 * installation. Drives the wizard's single Repository dropdown.
 * @public Consumed by `Cvoya.Spring.Connector.GitHub/web/*` cross-workspace.
 */
export type GitHubRepositoryResponse = Schemas["GitHubRepositoryResponse"];

/**
 * GET /api/v1/connectors/github/actions/list-collaborators item (#1133).
 * Drives the wizard's Reviewer dropdown — selecting a row stores
 * `login` on `UnitGitHubConfigRequest.reviewer`.
 * @public Consumed by `Cvoya.Spring.Connector.GitHub/web/*` cross-workspace.
 */
export type GitHubCollaboratorResponse = Schemas["GitHubCollaboratorResponse"];

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

/** GET /api/v1/units/{id}/expertise response body. */
export type AggregatedExpertiseResponse = Schemas["AggregatedExpertiseResponse"];

/** One row inside AggregatedExpertiseResponse.entries. */
export type AggregatedExpertiseEntryDto = Schemas["AggregatedExpertiseEntryDto"];

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
  | "ThreadStarted"
  | "ThreadCompleted"
  | "DecisionMade"
  | "ErrorOccurred"
  | "StateChanged"
  | "InitiativeTriggered"
  | "ReflectionCompleted"
  | "WorkflowStepCompleted"
  | "CostIncurred"
  | "TokenDelta"
  // T-04 (#946): per-step lifecycle updates from the backend
  // UnitValidationWorkflow. `source` is the unit's `unit://<name>`
  // address; `details` carries `{ step, status, code? }` where `step`
  // is one of `UnitValidationStep` and `status` is "Running" |
  // "Succeeded" | "Failed". The T-07 Validation panel reads this live
  // to animate the step checklist.
  | "ValidationProgress";

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

// ---------------------------------------------------------------------------
// Tenant tree (SVR-tenant-tree, umbrella #815).
// ---------------------------------------------------------------------------

/**
 * Response body for `GET /api/v1/tenant/tree`. Consumed by
 * `<UnitExplorer>` via the `useTenantTree()` hook — which also runs a
 * lenient-and-loud boundary validation pass before the payload reaches
 * the Explorer, so stray server values do not paint as silent
 * misrenders (see `FOUND-tree-boundary-validate`).
 */
export type TenantTreeResponse = Schemas["TenantTreeResponse"];

/** One node in the tenant tree. See {@link TenantTreeResponse}. */
export type TenantTreeNode = Schemas["TenantTreeNode"];

// ---------------------------------------------------------------------------
// Memories inspector (SVR-memories, umbrella #815).
// ---------------------------------------------------------------------------

/**
 * Response body for `GET /api/v1/units/{id}/memories` and
 * `GET /api/v1/agents/{id}/memories`. In v2.0 both lists are always
 * empty — the real backing store ships in `V21-memory-write`.
 */
export type MemoriesResponse = Schemas["MemoriesResponse"];

/** One memory entry. See {@link MemoriesResponse}. */
export type MemoryEntry = Schemas["MemoryEntry"];

/**
 * Response from `GET /api/v1/system/credentials/{provider}/status`
 * (#598). Reports whether an LLM provider's credentials (or endpoint,
 * for Ollama) are configured. The response NEVER contains the key
 * material itself — only booleans, the source tier, and an
 * operator-facing suggestion string.
 */
export interface ProviderCredentialStatusResponse {
  /** Canonical provider id (anthropic / openai / google / ollama). */
  provider: string;
  /**
   * True when the platform can obtain the credential at dispatch time.
   * For Ollama, true when the configured base URL responded to a health
   * probe.
   */
  resolvable: boolean;
  /**
   * Which tier produced the credential — `"unit"` or `"tenant"` — when
   * `resolvable` is true; `null` otherwise (and always null for Ollama).
   */
  source: "unit" | "tenant" | null;
  /**
   * Operator-facing hint to surface in the "not configured" UI state.
   * Null when the credential is already resolvable.
   */
  suggestion: string | null;
}
