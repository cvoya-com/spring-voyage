// Wizard-side constants for execution-tool and hosting-mode selection.
//
// Provider/model catalogs are sourced exclusively from the agent-runtimes
// endpoint (`GET /api/v1/agent-runtimes` + `GET /api/v1/agent-runtimes/{id}/models`)
// via the `useAgentRuntimes` / `useAgentRuntimeModels` hooks in
// `@/lib/api/queries`. The hardcoded `AI_PROVIDERS` catalog that used to
// live here was retired in #735 once the last consumer
// (`membership-dialog.tsx`, `execution-panel.tsx`, `execution-tab.tsx`)
// migrated onto the runtimes endpoint.

/** Execution tool identifiers — determines which agent runtime processes work. */
export type ExecutionTool =
  | "claude-code"
  | "codex"
  | "gemini"
  | "spring-voyage"
  | "custom";

export const EXECUTION_TOOLS: readonly {
  id: ExecutionTool;
  label: string;
}[] = [
  { id: "claude-code", label: "Claude Code" },
  { id: "codex", label: "Codex (OpenAI)" },
  { id: "gemini", label: "Gemini (Google)" },
  { id: "spring-voyage", label: "Spring Voyage Agent" },
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

/**
 * Maps an execution tool to the canonical runtime id the wizard and
 * related surfaces resolve via the agent-runtimes endpoint. Non-Spring-
 * Voyage tools hardcode their provider inside the CLI (Claude Code →
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
    case "spring-voyage":
    case "custom":
    default:
      return null;
  }
}

/**
 * Resolves the agent-runtime registry ID (e.g. "ollama", "claude") from the
 * wizard's tool + provider pair. For fixed-provider tools (claude-code, codex,
 * gemini) the provider is implicit; for spring-voyage the operator-chosen
 * provider IS the registry key (with "anthropic" normalised to "claude").
 * Returns null for custom tools or when provider is absent.
 */
export function getAgentRegistryId(
  tool: ExecutionTool,
  provider: string,
): string | null {
  switch (tool) {
    case "claude-code":
      return "claude";
    case "codex":
      return "openai";
    case "gemini":
      return "google";
    case "spring-voyage": {
      const normalised = provider.trim().toLowerCase();
      if (!normalised) return null;
      return normalised === "anthropic" ? "claude" : normalised;
    }
    case "custom":
    default:
      return null;
  }
}

/**
 * Maps an execution tool to the wire-level `provider` field the unit
 * creation endpoint expects. `spring-voyage` passes the explicit provider
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
    case "spring-voyage":
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
