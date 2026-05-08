// Agent-create page wizard state persistence (#1894).
//
// The standalone /agents/create page can be interrupted by a hard refresh
// or an external redirect. Persist a small, secrets-free snapshot of the
// operator-entered fields in sessionStorage so the page can restore them
// when it remounts. The unit-tab AgentCreateDialog is intentionally not
// wired to this module; dialog state is ephemeral.

export const AGENT_WIZARD_STATE_SCHEMA_VERSION = 1;
export const AGENT_WIZARD_SESSION_KEY = "spring.agent-create.v1";

export type AgentWizardSource = "scratch" | "from-package" | "browse";

export interface AgentWizardSnapshot {
  schemaVersion: typeof AGENT_WIZARD_STATE_SCHEMA_VERSION;
  source?: AgentWizardSource;
  name?: string;
  displayName?: string;
  description?: string;
  role?: string;
  runtime?: string;
  modelProviderId?: string;
  modelId?: string;
  hosting?: string;
  image?: string;
}

const OPTIONAL_STRING_FIELDS = [
  "name",
  "displayName",
  "description",
  "role",
  "runtime",
  "modelProviderId",
  "modelId",
  "hosting",
  "image",
] satisfies ReadonlyArray<keyof AgentWizardSnapshot>;

function resolveStorage(storage?: Storage): Storage | null {
  if (storage) return storage;
  if (typeof window === "undefined") return null;
  return window.sessionStorage;
}

/**
 * Type-guard a parsed JSON blob into an `AgentWizardSnapshot`. Returns
 * `null` for any structural mismatch so the page treats stale or corrupt
 * blobs as absent and mounts a fresh form.
 */
export function validateAgentSnapshot(
  blob: unknown,
): AgentWizardSnapshot | null {
  if (blob === null || typeof blob !== "object") return null;
  const candidate = blob as Record<string, unknown>;
  if (candidate.schemaVersion !== AGENT_WIZARD_STATE_SCHEMA_VERSION) {
    return null;
  }

  const source = candidate.source;
  if (
    source !== undefined &&
    source !== "scratch" &&
    source !== "from-package" &&
    source !== "browse"
  ) {
    return null;
  }

  for (const key of OPTIONAL_STRING_FIELDS) {
    const value = candidate[key];
    if (value !== undefined && typeof value !== "string") {
      return null;
    }
  }

  return {
    schemaVersion: AGENT_WIZARD_STATE_SCHEMA_VERSION,
    ...(source === undefined ? {} : { source: source as AgentWizardSource }),
    ...Object.fromEntries(
      OPTIONAL_STRING_FIELDS.flatMap((key) => {
        const value = candidate[key];
        return value === undefined ? [] : [[key, value]];
      }),
    ),
  } as AgentWizardSnapshot;
}

export function loadAgentWizardSnapshot(
  storage?: Storage,
): AgentWizardSnapshot | null {
  const store = resolveStorage(storage);
  if (store === null) return null;

  let raw: string | null;
  try {
    raw = store.getItem(AGENT_WIZARD_SESSION_KEY);
  } catch {
    return null;
  }
  if (raw === null) return null;

  let parsed: unknown;
  try {
    parsed = JSON.parse(raw);
  } catch {
    return null;
  }

  return validateAgentSnapshot(parsed);
}

export function saveAgentWizardSnapshot(
  snapshot: AgentWizardSnapshot,
  storage?: Storage,
): void {
  const store = resolveStorage(storage);
  if (store === null) return;

  try {
    store.setItem(AGENT_WIZARD_SESSION_KEY, JSON.stringify(snapshot));
  } catch {
    // Best-effort persistence; the in-memory form remains canonical.
  }
}

export function clearAgentWizardSnapshot(storage?: Storage): void {
  const store = resolveStorage(storage);
  if (store === null) return;

  try {
    store.removeItem(AGENT_WIZARD_SESSION_KEY);
  } catch {
    // See `saveAgentWizardSnapshot`.
  }
}
