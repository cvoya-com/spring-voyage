/**
 * Runtime constants pinned for the suite.
 *
 * v0.1 ships several agent runtimes (claude-code, codex, gemini, and the
 * platform-managed `spring-voyage` runtime). Tests in this suite
 * exclusively use:
 *
 *   - `spring-voyage` for the agent runtime. It is the platform-managed
 *     runtime ("our local implementation" — formerly `dapr-agent`) that
 *     drives any installed model provider, with no third-party CLI
 *     dependency and no inter-process spawn cost. It is the only runtime
 *     that allows the credential-free `ollama` provider; every other
 *     runtime fixes a credentialed provider (claude-code → anthropic,
 *     etc.). See `src/Cvoya.Spring.Web/src/lib/ai-models.ts`.
 *
 *   - `ollama` for the model provider. Its credential edge is
 *     `authMethod: null` (no credential), so tests don't need to
 *     pre-create tenant secrets just to drive the wizard past its
 *     credential step.
 *
 * Operators wanting to test other runtimes should fork specs and override
 * the constants below — but the default pin stays here so the suite has
 * one obvious answer to "what runtime are these tests pretending to be?".
 */

/**
 * Agent-runtime dropdown value (`<option value=…>`). The canonical
 * registry id for the platform-managed runtime is `spring-voyage`
 * (the legacy `dapr-agent` id was retired).
 */
export const AGENT_ID = "spring-voyage";

/** Model-provider dropdown value when the runtime is `spring-voyage`. */
export const PROVIDER_ID = "ollama";

/** Default Ollama model — must be present on the local Ollama server. */
export const DEFAULT_MODEL =
  process.env.E2E_PORTAL_OLLAMA_MODEL?.trim() || "llama3.2:3b";

/** Hosting mode — `ephemeral` is the v0.1 default and avoids container plumbing. */
export const HOSTING_MODE = "ephemeral";

/** Local Ollama base URL used for the `--llm` reachability probe. */
export const OLLAMA_BASE_URL =
  process.env.LLM_BASE_URL?.trim() ||
  process.env.LanguageModel__Ollama__BaseUrl?.trim() ||
  "http://localhost:11434";
