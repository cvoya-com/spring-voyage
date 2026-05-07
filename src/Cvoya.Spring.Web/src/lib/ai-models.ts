// Wizard-side constants for execution-tool and hosting-mode selection.
//
// Provider/model catalogs are sourced exclusively from the agent-runtimes
// endpoint (`GET /api/v1/agent-runtimes` + `GET /api/v1/agent-runtimes/{id}/models`)
// via the `useAgentRuntimes` / `useAgentRuntimeModels` hooks in
// `@/lib/api/queries`. The hardcoded `AI_PROVIDERS` catalog that used to
// live here was retired in #735 once the last consumer
// (`membership-dialog.tsx`, `execution-panel.tsx`, `execution-tab.tsx`)
// migrated onto the runtimes endpoint.

/** Agent runtime identifiers — determines which agent runtime processes work. */
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

// Legacy helpers `getToolWireProvider` and `getRuntimeSecretName` were
// retired in ADR-0038 (PR-1b). Both mapped the pre-ADR wire shape:
// the former synthesised the wire-level `provider` slot from
// (tool, runtime), and the latter mapped a runtime id to the
// `(tenant, runtime-id)`-keyed secret name. ADR-0038 §6 re-keys
// credentials to `(tenant, provider, authMethod)`, so the secret name
// is no longer derivable from a runtime id; PR-3 will rebuild
// per-provider credential UX on top of `runtime-catalog.yaml`.
