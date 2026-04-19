// Central catalog of AI providers and their models, used by the unit-creation
// wizard (Bug #258). The authoritative source at runtime is the server's
// `GET /api/v1/models/{provider}` endpoint (#597) — the wizard queries it via
// `useProviderModels` and falls back to the list below when the request fails
// (e.g. anonymous / offline dev session).
//
// IMPORTANT: the default provider/model pair must match the platform-wide
// default in `Cvoya.Spring.Dapr/Execution/AiProviderOptions.cs`. Keep them in
// sync so users who accept the wizard defaults write a model string the server
// actually supports. Provider/model names also drive the server-side static
// fallback in `Cvoya.Spring.Dapr/Execution/ModelCatalog.cs` — update both
// files together when adjusting the known-good list.
//
// To add a provider or a model, edit the array below. The UI renders the first
// entry in PROVIDERS as the default; the first model in each provider is the
// default when that provider is selected.

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

export const AI_PROVIDERS: readonly AiProvider[] = [
  {
    id: "claude",
    displayName: "Anthropic Claude",
    models: [
      // Default — matches AiProviderOptions.Model.
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

/** The default provider shown when the wizard opens. */
export const DEFAULT_PROVIDER_ID = AI_PROVIDERS[0].id;

/** The default model shown when the wizard opens. */
export const DEFAULT_MODEL = AI_PROVIDERS[0].models[0];

/**
 * Looks up a provider by id. Returns the first provider as a fallback so the
 * wizard always has something to render even when the stored preference
 * references a provider that's no longer in the catalog.
 */
export function getProvider(id: string): AiProvider {
  return AI_PROVIDERS.find((p) => p.id === id) ?? AI_PROVIDERS[0];
}

/**
 * Maps a non-dapr-agent execution tool to the provider id whose model
 * catalog the tool draws from (#641). Non-Dapr-Agent tools hardcode
 * their provider inside the CLI (Claude Code → Anthropic, Codex →
 * OpenAI, Gemini → Google), but operators still need to pick which
 * model inside that family to run — that choice lives in the wizard's
 * Model dropdown even when the Provider dropdown is hidden.
 *
 * Returns:
 *   - `claude-code` → "claude"
 *   - `codex`       → "openai"
 *   - `gemini`      → "google"
 *   - `custom`      → null (no finite known catalog — see #641 scope)
 *   - `dapr-agent`  → null (caller branches on the Provider dropdown
 *     directly; this helper is for tools that hide Provider)
 *
 * The returned id is the same key used by <c>getProvider()</c> and the
 * server's <c>GET /api/v1/models/{provider}</c> endpoint, so the
 * wizard can reuse <c>useProviderModels</c> without a second mapping.
 */
export function getToolModelProvider(tool: ExecutionTool): string | null {
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
