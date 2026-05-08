// Shared helper for the portal's agent-create flows (#1040).
//
// Two surfaces use this module:
//
//  - `src/app/agents/create/page.tsx` — the standalone wizard at
//    `/agents/create`. Mirrors `spring agent create` field-for-field.
//  - `src/components/units/inline-create-agent-dialog.tsx` — the
//    inline-create dialog reachable from a unit's Agents tab. Defaults
//    the unit assignment to the current unit so the new agent appears in
//    the unit's member list immediately on success.
//
// Both paths funnel through `buildCreateAgentRequest` so the wire body
// (displayName + description + role + unitIds + definitionJson)
// and the validation rules are owned in exactly one place. Keeping this
// outside React (`useMutation` lives at the call site) means the helper
// can be imported by the inline dialog without dragging the standalone
// page's wizard chrome along.

import type { CreateAgentRequest } from "@/lib/api/types";

/** URL-safe id pattern. Used by the package wizard for package-name validation. */
export const AGENT_NAME_PATTERN = /^[a-z0-9-]+$/;

/**
 * Free-form input shape both the standalone page and the inline dialog
 * collect from the user. Empty strings are treated as "not supplied"
 * before the wire body is assembled.
 */
export interface AgentCreateFormInput {
  /** Human-readable label (server's `DisplayName`). */
  displayName: string;
  /** Optional free text mirrored into `Description`; empty by default. */
  description?: string;
  /** Optional free text. `null` is the cleared state. */
  role?: string | null;
  /**
   * Container image reference (`execution.image`). Defaulted into the
   * agent definition document when supplied.
   */
  image?: string;
  /**
   * Container runtime key (`execution.runtime`) — `docker` / `podman`.
   * Defaulted into the agent definition document when supplied.
   */
  runtime?: string;
  /**
   * ADR-0038 agent runtime id (`ai.runtime`) — `claude-code`, `codex`,
   * `gemini`, `spring-voyage`, `custom`. Defaulted into the agent
   * definition document when supplied.
   */
  tool?: string;
  /**
   * ADR-0038 structured model selector — `{ provider, id }`. The
   * provider id (e.g. `anthropic`, `ollama`) names the model-provider
   * install; the model id is pulled from that provider's
   * `/api/v1/tenant/model-providers/installs/{id}/models` catalogue
   * by the caller. Both halves are emitted under
   * `execution.model = { provider, id }`.
   */
  model?: { provider: string; id: string };
  /**
   * Initial unit assignments. Server requires ≥1 (#744). Both portal
   * surfaces enforce this client-side so we never POST a request the
   * server is guaranteed to reject.
   */
  unitIds: string[];
}

/**
 * Validation outcome. The portal surfaces a single inline message at a
 * time (per `DESIGN.md` § form-validation), so the helper returns the
 * first failing rule rather than collecting them. `null` means the
 * input is acceptable.
 */
export type AgentCreateValidationError =
  | "displayName-required"
  | "unit-required";

const VALIDATION_MESSAGES: Record<AgentCreateValidationError, string> = {
  "displayName-required": "Display name is required.",
  "unit-required": "Pick at least one unit to assign the agent to.",
};

/**
 * Run the field-level validations both surfaces share. Returns the
 * first failing rule, or `null` when the input is well-formed.
 */
export function validateAgentCreateInput(
  input: AgentCreateFormInput,
): AgentCreateValidationError | null {
  const displayName = (input.displayName ?? "").trim();
  if (displayName.length === 0) return "displayName-required";

  const units = (input.unitIds ?? [])
    .map((u) => u.trim())
    .filter((u) => u.length > 0);
  if (units.length === 0) return "unit-required";

  return null;
}

/** Look up the operator-facing copy for a validation key. */
export function describeAgentCreateError(
  error: AgentCreateValidationError,
): string {
  return VALIDATION_MESSAGES[error];
}

/**
 * Build the agent-definition JSON document from the supplied execution
 * shorthands. Returns `null` when no shorthand was supplied — letting
 * the caller omit `definitionJson` from the wire body so the request
 * looks identical to a CLI `spring agent create` with no `--image /
 * --runtime / --tool / --model-provider / --model` flags. The shape
 * mirrors the CLI's `MergeExecutionShorthand` (`AgentCommand.cs`) so
 * the on-disk `AgentDefinitions.Definition` blob is byte-for-byte the
 * same whether the agent was created from the portal or from the CLI.
 *
 * ADR-0038: `model` is the structured `{ provider, id }` pair and lands
 * under `execution.model` as an object. A model with both halves blank
 * is treated as "not supplied" and is omitted entirely; supplying only
 * one half emits just that field on the nested object so partial input
 * still round-trips through `MergeExecutionShorthand`.
 */
export function buildAgentDefinitionJson(input: {
  image?: string;
  runtime?: string;
  tool?: string;
  model?: { provider: string; id: string };
}): string | null {
  const exec: Record<string, string | { provider?: string; id?: string }> = {};
  const image = input.image?.trim();
  const runtime = input.runtime?.trim();
  const tool = input.tool?.trim();
  const provider = input.model?.provider?.trim();
  const modelId = input.model?.id?.trim();
  if (image) exec.image = image;
  if (runtime) exec.runtime = runtime;
  if (tool) exec.tool = tool;
  if (provider || modelId) {
    const model: { provider?: string; id?: string } = {};
    if (provider) model.provider = provider;
    if (modelId) model.id = modelId;
    exec.model = model;
  }
  if (Object.keys(exec).length === 0) return null;
  return JSON.stringify({ execution: exec });
}

/**
 * Assemble the wire body for `POST /api/v1/agents` from the validated
 * form input. Throws if validation has not been run; callers are
 * expected to gate on `validateAgentCreateInput` first.
 */
export function buildCreateAgentRequest(
  input: AgentCreateFormInput,
): CreateAgentRequest {
  const validation = validateAgentCreateInput(input);
  if (validation !== null) {
    throw new Error(describeAgentCreateError(validation));
  }

  const displayName = input.displayName.trim();
  const role = input.role?.trim() ? input.role.trim() : null;
  const description = input.description?.trim() ?? "";
  const unitIds = input.unitIds
    .map((u) => u.trim())
    .filter((u) => u.length > 0);

  const definitionJson = buildAgentDefinitionJson({
    image: input.image,
    runtime: input.runtime,
    tool: input.tool,
    model: input.model,
  });

  const body: CreateAgentRequest = {
    displayName,
    description,
    role,
    unitIds,
    ...(definitionJson !== null ? { definitionJson } : {}),
  };
  return body;
}
