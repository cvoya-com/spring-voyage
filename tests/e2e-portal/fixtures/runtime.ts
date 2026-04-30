/**
 * Runtime constants pinned for the suite.
 *
 * v0.1 ships several agent runtimes (Claude, OpenAI, Google, Ollama, Dapr
 * Agent). Tests in this suite exclusively use:
 *
 *   - `dapr-agent` for the execution tool. It is the v0.1 reference
 *     `dapr-agent` runtime ("our local implementation") — no third-party
 *     CLI dependencies, no inter-process spawn cost, no per-tenant
 *     credential plumbing surface for the test harness to manage.
 *
 *   - `ollama` for the LLM provider. It is the only credential-free
 *     runtime (`CredentialKind === "None"`) v0.1 ships, so tests don't
 *     need to pre-create tenant secrets just to drive the wizard
 *     past its credential step.
 *
 * Operators wanting to test other runtimes should fork specs and override
 * the constants below — but the default pin stays here so the suite has
 * one obvious answer to "what runtime are these tests pretending to be?".
 */

/** Wizard execution-tool dropdown value (`<option value=…>`). */
export const TOOL_ID = "dapr-agent";

/** LLM-provider dropdown value when tool is dapr-agent. */
export const PROVIDER_ID = "ollama";

/** Default Ollama model — must be present on the local Ollama server. */
export const DEFAULT_MODEL =
  process.env.E2E_PORTAL_OLLAMA_MODEL?.trim() || "llama3.2";

/** Hosting mode — `ephemeral` is the v0.1 default and avoids container plumbing. */
export const HOSTING_MODE = "ephemeral";

/** Local Ollama base URL used for the `--llm` reachability probe. */
export const OLLAMA_BASE_URL =
  process.env.LLM_BASE_URL?.trim() ||
  process.env.LanguageModel__Ollama__BaseUrl?.trim() ||
  "http://localhost:11434";
