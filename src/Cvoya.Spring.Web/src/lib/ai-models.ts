// Wizard-side runtime + hosting catalogue (ADR-0038).
//
// ADR-0038 split the agent-execution stack into three identities:
//   - AgentRuntime (`runtime` on the wire / manifest): the launcher key —
//     `claude-code`, `codex`, `gemini`, `spring-voyage`, or a future
//     `custom` entry from `runtime-catalog.yaml`.
//   - ModelProvider: the LLM API surface — `anthropic`, `openai`,
//     `google`, `ollama`, …  Surfaced via the
//     `/api/v1/tenant/model-providers/installs` endpoint and consumed
//     here as `InstalledModelProviderResponse`.
//   - Model: a `{provider, id}` pair. Provider is intrinsic; there is no
//     separate `provider` slot on the wire.
//
// The portal renders one or both of (runtime picker, provider picker)
// depending on `RUNTIMES[id].isProviderFixed`. When the runtime fixes
// its provider, the picker is hidden and the model dropdown is filtered
// to that provider's models. When the runtime is multi-provider
// (`spring-voyage`, future `custom`), the picker is rendered as a
// model-list filter; the wire format always carries `model.provider`
// alongside `model.id`.
//
// The fixed-provider mapping below mirrors the runtimes shipped in
// `runtime-catalog.yaml`. v0.2 adds a "custom" runtime + per-tenant
// catalogue overrides (#1761 follow-ups).

/** Agent runtime identifiers — ADR-0038 launcher keys. */
export type RuntimeId =
  | "claude-code"
  | "codex"
  | "gemini"
  | "spring-voyage"
  | "custom";

/** ADR-0038 closed set of provider ids the platform recognises. */
export type ProviderId = "anthropic" | "openai" | "google" | "ollama";

/**
 * Static catalogue mirror of `runtime-catalog.yaml` for the four
 * runtimes shipped in v0.1 plus the deferred `custom` slot. Each entry
 * is the minimum the wizard / panel UX needs to render:
 *   - `displayName` / `description` for the dropdown.
 *   - `isProviderFixed` — when true, the provider picker is hidden and
 *     the model dropdown is filtered to `fixedProvider`. When false the
 *     picker is rendered as a filter over `allowedProviders`.
 *   - `fixedProvider` — only meaningful when `isProviderFixed` is true.
 *   - `allowedProviders` — only meaningful when `isProviderFixed` is
 *     false. The wizard shows the intersection with the tenant's
 *     installed providers.
 *
 * v0.2 (#1761 follow-ups) replaces this static table with a live read
 * of `runtime-catalog.yaml` so per-tenant overrides land cleanly.
 */
export interface RuntimeDescriptor {
  id: RuntimeId;
  displayName: string;
  description: string;
  isProviderFixed: boolean;
  fixedProvider: ProviderId | null;
  allowedProviders: readonly ProviderId[];
  /**
   * Default container image for this runtime, mirrored from
   * `platform/runtime-catalog.yaml` (ADR-0038). The wizard pre-fills
   * the image field with this value when the operator selects this
   * runtime and has not yet edited the field. Empty string means
   * "no default" (e.g. the deferred `custom` runtime).
   */
  defaultImage: string;
}

export const RUNTIMES: Readonly<Record<RuntimeId, RuntimeDescriptor>> = {
  "claude-code": {
    id: "claude-code",
    displayName: "Claude Code",
    description:
      "Anthropic's Claude Code launcher. Provider is fixed to Anthropic.",
    isProviderFixed: true,
    fixedProvider: "anthropic",
    allowedProviders: ["anthropic"],
    defaultImage: "ghcr.io/cvoya-com/claude-code-base:latest",
  },
  codex: {
    id: "codex",
    displayName: "Codex (OpenAI)",
    description:
      "OpenAI's Codex CLI launcher. Provider is fixed to OpenAI.",
    isProviderFixed: true,
    fixedProvider: "openai",
    allowedProviders: ["openai"],
    defaultImage: "ghcr.io/cvoya-com/codex-base:latest",
  },
  gemini: {
    id: "gemini",
    displayName: "Gemini (Google)",
    description:
      "Google's Gemini CLI launcher. Provider is fixed to Google.",
    isProviderFixed: true,
    fixedProvider: "google",
    allowedProviders: ["google"],
    defaultImage: "ghcr.io/cvoya-com/gemini-base:latest",
  },
  "spring-voyage": {
    id: "spring-voyage",
    displayName: "Spring Voyage Agent",
    description:
      "Platform-managed agent that drives any installed model provider.",
    isProviderFixed: false,
    fixedProvider: null,
    allowedProviders: ["anthropic", "openai", "google", "ollama"],
    defaultImage: "ghcr.io/cvoya-com/spring-voyage-agent:latest",
  },
  custom: {
    id: "custom",
    displayName: "Custom",
    description:
      "Operator-supplied launcher (out of scope for v0.1; reserved).",
    isProviderFixed: false,
    fixedProvider: null,
    allowedProviders: [],
    defaultImage: "",
  },
};

export const RUNTIME_LIST: readonly RuntimeDescriptor[] = [
  RUNTIMES["claude-code"],
  RUNTIMES.codex,
  RUNTIMES.gemini,
  RUNTIMES["spring-voyage"],
  RUNTIMES.custom,
];

export const DEFAULT_RUNTIME_ID: RuntimeId = "claude-code";

/** Agent hosting mode — how long the agent process lives. */
export type HostingMode = "ephemeral" | "persistent";

export const HOSTING_MODES: readonly { id: HostingMode; label: string }[] = [
  { id: "persistent", label: "Persistent" },
  { id: "ephemeral", label: "Ephemeral" },
];

export const DEFAULT_HOSTING_MODE: HostingMode = "persistent";

/**
 * Returns true when the runtime's provider is fixed by the launcher
 * itself (claude-code → anthropic, codex → openai, gemini → google).
 * The wizard / Execution panel hides the provider picker in that case.
 */
export function isRuntimeProviderFixed(runtimeId: string | null): boolean {
  if (runtimeId === null) return false;
  const descriptor = RUNTIMES[runtimeId as RuntimeId];
  if (!descriptor) return false;
  return descriptor.isProviderFixed;
}

/**
 * Resolves the provider id implied by a fixed-provider runtime, or
 * `null` for multi-provider runtimes / unknown ids. Callers use this to
 * default the model dropdown's filter when the picker is hidden.
 */
export function getFixedProvider(
  runtimeId: string | null,
): ProviderId | null {
  if (runtimeId === null) return null;
  const descriptor = RUNTIMES[runtimeId as RuntimeId];
  if (!descriptor) return null;
  return descriptor.fixedProvider;
}

/**
 * Resolves the providers a runtime is allowed to dispatch against.
 * Returns `null` when the runtime id is unknown — callers fall back to
 * "every installed provider".
 */
export function getAllowedProviders(
  runtimeId: string | null,
): readonly ProviderId[] | null {
  if (runtimeId === null) return null;
  const descriptor = RUNTIMES[runtimeId as RuntimeId];
  if (!descriptor) return null;
  return descriptor.allowedProviders;
}
