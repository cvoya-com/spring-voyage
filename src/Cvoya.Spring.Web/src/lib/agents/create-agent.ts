// Shared helper for the portal's agent-create flows (#1040).
//
// Two surfaces use this module:
//
//  - `src/app/agents/create/page.tsx` — the standalone wizard at
//    `/agents/create`. Mirrors `spring agent create` field-for-field.
//  - `src/components/agents/create-dialog.tsx` — the inline-create dialog
//    reachable from a unit's Agents tab. Defaults the unit assignment to
//    the current unit so the new agent appears in the unit's member list
//    immediately on success.
//
// Both paths funnel through `buildCreateAgentRequest` so the wire body
// (displayName + description + role + unitIds + definitionJson) is owned
// in exactly one place. Keeping this outside React (`useMutation` lives
// at the call site) means the helper can be imported by the inline dialog
// without dragging the standalone page's wizard chrome along.

import type { CreateAgentRequest } from "@/lib/api/types";

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
   * Container image reference (`definitionJson.execution.image`). Defaulted
   * into the agent definition document when supplied.
   */
  image?: string;
  /**
   * ADR-0038 agent runtime id (`definitionJson.runtime`) — `claude-code`,
   * `codex`, `gemini`, `spring-voyage`, `custom`. Defaulted into the agent
   * definition document when supplied.
   */
  runtime?: string;
  /**
   * ADR-0038 structured model selector — `{ provider, id }`. The
   * provider id (e.g. `anthropic`, `ollama`) names the model-provider
   * install; the model id is pulled from that provider's
   * `/api/v1/tenant/model-providers/installs/{id}/models` catalogue
   * by the caller. Both halves are emitted under
   * `definitionJson.model = { provider, id }`.
   */
  model?: { provider: string; id: string };
  /**
   * ADR-0039 agent-owned hosting mode (`definitionJson.execution.hosting`).
   * One of:
   *
   * - `'ephemeral'` — the agent process exits after each turn.
   * - `'persistent'` — the agent process is long-lived.
   * - `null` — inherit from the parent unit at dispatch time. The field
   *   is omitted entirely from the serialised JSON in this case (no
   *   `"hosting": null` lands on disk).
   *
   * Distinct from `undefined` only at the type level; both are treated
   * as "not supplied" and produce no `execution.hosting` key.
   */
  hosting?: "ephemeral" | "persistent" | null;
  /**
   * Initial unit assignments. An empty list creates a top-level
   * tenant-parented agent per ADR-0039 §6.
   */
  unitIds: string[];
}

/**
 * Validation outcome. The portal surfaces a single inline message at a
 * time (per `DESIGN.md` § form-validation), so the helper returns the
 * first failing rule rather than collecting them. `null` means the
 * input is acceptable.
 */
export type AgentCreateValidationError = "displayName-required";

const VALIDATION_MESSAGES: Record<AgentCreateValidationError, string> = {
  "displayName-required": "Display name is required.",
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
 * shorthands. Returns `null` when no shorthand was supplied, which the
 * direct create endpoint treats as pure inheritance from tenant defaults
 * or selected parent units.
 *
 * ADR-0038 / ADR-0039 K6: `runtime` and the structured `model` selector
 * live at the definition root; `image` and `hosting` stay under
 * `execution`. A model is emitted only when both `provider` and `id` are
 * present; a half-filled model remains inherited.
 *
 * ADR-0039 (I2): `hosting` is the agent-owned hosting mode. `null` (or
 * `undefined`) means "inherit from parent" and the field is omitted from
 * the serialised JSON entirely; `'ephemeral'` and `'persistent'` land
 * verbatim under `execution.hosting`.
 */
export function buildAgentDefinitionJson(input: {
  image?: string;
  runtime?: string;
  model?: { provider: string; id: string };
  hosting?: "ephemeral" | "persistent" | null;
}): string | null {
  const payload: {
    runtime?: string;
    model?: { provider?: string; id?: string };
    execution?: { image?: string; hosting?: "ephemeral" | "persistent" };
  } = {};
  const execution: { image?: string; hosting?: "ephemeral" | "persistent" } = {};
  const image = input.image?.trim();
  const runtime = input.runtime?.trim();
  const provider = input.model?.provider?.trim();
  const modelId = input.model?.id?.trim();
  if (runtime) payload.runtime = runtime;
  if (provider && modelId) {
    payload.model = { provider, id: modelId };
  }
  if (image) execution.image = image;
  if (input.hosting === "ephemeral" || input.hosting === "persistent") {
    execution.hosting = input.hosting;
  }
  if (Object.keys(execution).length > 0) {
    payload.execution = execution;
  }
  if (Object.keys(payload).length === 0) return null;
  return JSON.stringify(payload);
}

/**
 * Assemble the wire body for `POST /api/v1/tenant/agents` from the
 * validated form input. Throws if validation has not been run; callers
 * are expected to gate on `validateAgentCreateInput` first.
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
    model: input.model,
    hosting: input.hosting,
  });

  const body: CreateAgentRequest = {
    displayName,
    description,
    role,
    unitIds,
    definitionJson,
  };
  return body;
}
