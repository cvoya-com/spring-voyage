// Wizard-side constants for execution-tool and hosting-mode selection.
//
// After #690 the unit-creation wizard reads its provider/model lists
// from `GET /api/v1/agent-runtimes` + `GET /api/v1/agent-runtimes/{id}/models`
// instead of `AI_PROVIDERS` below. The constants stay in place for
// `membership-dialog.tsx`, `execution-panel.tsx`, and `execution-tab.tsx`,
// which still consume the hardcoded list — migrating those surfaces
// onto the runtimes endpoint is tracked as a follow-up.

export interface AiProvider {
  /** Machine identifier used by the server, e.g. `claude`, `openai`. */
  readonly id: string;
  /** Human-readable label shown in the provider dropdown. */
  readonly displayName: string;
  /** Model identifiers this provider supports (first entry is the default). */
  readonly models: readonly string[];
}

/** Execution tool identifiers — determines which agent runtime processes work. */
export type ExecutionTool =
  | "claude-code"
  | "codex"
  | "gemini"
  | "dapr-agent"
  | "custom";

export const EXECUTION_TOOLS: readonly {
  id: ExecutionTool;
  label: string;
}[] = [
  { id: "claude-code", label: "Claude Code" },
  { id: "codex", label: "Codex (OpenAI)" },
  { id: "gemini", label: "Gemini (Google)" },
  { id: "dapr-agent", label: "Dapr Agent" },
  { id: "custom", label: "Custom" },
];

export const DEFAULT_EXECUTION_TOOL: ExecutionTool = "claude-code";

/** Agent hosting mode — how long the agent process lives. */
export type HostingMode = "ephemeral" | "persistent";

export const HOSTING_MODES: readonly { id: HostingMode; label: string }[] = [
  { id: "ephemeral", label: "Ephemeral" },
  { id: "persistent", label: "Persistent" },
];

export const DEFAULT_HOSTING_MODE: HostingMode = "ephemeral";

// Fallback catalog consumed by `membership-dialog.tsx` / `execution-panel.tsx`
// / `execution-tab.tsx` until those surfaces migrate to the agent-runtimes
// endpoint. The wizard itself no longer reads this list.
export const AI_PROVIDERS: readonly AiProvider[] = [
  {
    id: "claude",
    displayName: "Anthropic Claude",
    models: [
      "claude-sonnet-4-20250514",
      "claude-opus-4-20250514",
      "claude-haiku-4-20250514",
    ],
  },
  {
    id: "openai",
    displayName: "OpenAI",
    models: ["gpt-4o", "gpt-4o-mini", "o3-mini"],
  },
  {
    id: "google",
    displayName: "Google AI",
    models: ["gemini-2.5-pro", "gemini-2.5-flash"],
  },
  {
    id: "ollama",
    displayName: "Ollama",
    models: [
      "qwen2.5:14b",
      "llama3.2:3b",
      "llama3.1:8b",
      "mistral:7b",
      "deepseek-coder-v2:16b",
    ],
  },
];

export const DEFAULT_PROVIDER_ID = AI_PROVIDERS[0].id;

export const DEFAULT_MODEL = AI_PROVIDERS[0].models[0];

export function getProvider(id: string): AiProvider {
  return AI_PROVIDERS.find((p) => p.id === id) ?? AI_PROVIDERS[0];
}

/**
 * Maps an execution tool to the canonical runtime id the wizard and
 * related surfaces resolve via the agent-runtimes endpoint. Non-Dapr-
 * Agent tools hardcode their provider inside the CLI (Claude Code →
 * Anthropic, Codex → OpenAI, Gemini → Google), so callers can still
 * surface a Model dropdown by routing through the matching runtime.
 */
export function getToolRuntimeId(tool: ExecutionTool): string | null {
  switch (tool) {
    case "claude-code":
      return "claude";
    case "codex":
      return "openai";
    case "gemini":
      return "google";
    case "dapr-agent":
    case "custom":
    default:
      return null;
  }
}

/**
 * Legacy alias kept for `execution-panel.tsx` / `execution-tab.tsx`
 * until those surfaces migrate onto the runtimes endpoint. Mirrors
 * `getToolRuntimeId` because the two concepts collapsed after #690.
 */
export function getToolModelProvider(tool: ExecutionTool): string | null {
  return getToolRuntimeId(tool);
}

/**
 * Maps an execution tool to the wire-level `provider` field the unit
 * creation endpoint expects. `dapr-agent` passes the explicit provider
 * the caller picked; all other tools have a fixed provider derived from
 * the CLI they drive.
 */
export function getToolWireProvider(
  tool: ExecutionTool,
  runtimeId: string | null,
): string | undefined {
  switch (tool) {
    case "claude-code":
      return "claude";
    case "codex":
      return "openai";
    case "gemini":
      return "google";
    case "dapr-agent":
      return runtimeId ?? undefined;
    case "custom":
    default:
      return undefined;
  }
}

/**
 * Maps a runtime id to the secret name tier-2 resolution looks up in
 * the secret registry. The wizard writes the operator-supplied key
 * under this name (either unit- or tenant-scoped) so downstream
 * dispatch resolves through `ILlmCredentialResolver` without further
 * config. Returns `null` when the runtime requires no credential
 * (e.g. local Ollama).
 */
export function getRuntimeSecretName(runtimeId: string): string | null {
  switch (runtimeId) {
    case "claude":
    case "anthropic":
      return "anthropic-api-key";
    case "openai":
      return "openai-api-key";
    case "google":
    case "gemini":
    case "googleai":
      return "google-api-key";
    default:
      return null;
  }
}
