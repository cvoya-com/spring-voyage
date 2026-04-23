// Wizard state persistence (#1132).
//
// The new-unit wizard previously kept all state in React component
// memory, so a hard refresh — exactly the thing operators do after
// installing the GitHub App on github.com and being redirected back —
// dropped them at step 1 with every field cleared. This module owns the
// `sessionStorage` round-trip: it serialises a stable subset of the
// wizard's form state (excluding secrets) under a per-tab/per-run key,
// validates rehydrated blobs against a schema, and provides a single
// place to clear the slot when a unit is created or the operator backs
// out.
//
// Design choices:
//
//   * sessionStorage, not localStorage — we want the cache to die with
//     the tab, not survive across browser windows or sessions where
//     stale state could collide with concurrent wizard runs.
//   * A per-run UUID keys the slot so each wizard tab is independent.
//     The id is generated lazily on mount and held in memory; it does
//     not currently round-trip through the URL (the issue allowed for
//     either approach).
//   * A schema version constant lets us evolve the snapshot shape
//     without crashing operators on older blobs — bumping
//     WIZARD_STATE_SCHEMA_VERSION causes every existing blob to be
//     discarded silently on rehydrate.
//   * Secrets are NEVER persisted. The wizard collects API keys, OAuth
//     codes, and secret values; we exclude them from the snapshot type
//     (they are simply not part of `WizardSnapshot`) and the wizard
//     re-fetches credential status from the server after rehydrate.

const SESSION_KEY_PREFIX = "spring.wizard.unit-create.";
const RUN_ID_KEY = `${SESSION_KEY_PREFIX}run-id`;

/**
 * Bump this when the snapshot shape changes incompatibly. On rehydrate,
 * a non-matching version is treated as a missing snapshot — the operator
 * starts fresh at step 1 instead of seeing a half-rehydrated wizard.
 */
export const WIZARD_STATE_SCHEMA_VERSION = 1;

export type WizardStep = 1 | 2 | 3 | 4 | 5 | 6;
export type WizardMode = "template" | "scratch" | "yaml";

/**
 * Stable, secrets-free subset of the wizard's form state. New fields
 * MUST be added with a sensible default in `validateSnapshot` (or via a
 * schema-version bump) so older blobs continue to rehydrate cleanly —
 * operators don't lose work just because we added a field.
 *
 * Notably absent (do not add without re-reading #1132):
 *   * `secrets` — pending unit-scoped secret values (raw plaintext).
 *   * `credentialKey` / `saveAsTenantDefault` / `credentialOverrideOpen`
 *     — all part of the LLM API-key inline-entry flow on Step 2/5.
 *   * Any OAuth code, GitHub installation token, or other key material.
 *
 * Mid-mutation flags (`createdUnitName`, `startRequested`, `submitting`)
 * are also excluded — rehydrating them would put the wizard in the
 * "waiting for 201" state with no in-flight request.
 */
export interface WizardSnapshot {
  schemaVersion: typeof WIZARD_STATE_SCHEMA_VERSION;
  currentStep: WizardStep;
  form: WizardFormSnapshot;
}

/**
 * Form fields persisted alongside the step. Every field here is a plain
 * scalar or a JSON-safe collection — `unknown` for the connector config
 * keeps the snapshot opaque to per-connector schemas (the connector's
 * own server-side validation runs at create time).
 */
export interface WizardFormSnapshot {
  name: string;
  displayName: string;
  description: string;
  provider: string;
  model: string;
  color: string;
  tool: string;
  hosting: string;
  image: string;
  runtime: string;
  mode: WizardMode | null;
  templateId: string | null;
  yamlText: string;
  yamlFileName: string | null;
  connectorSlug: string | null;
  connectorTypeId: string | null;
  connectorConfig: Record<string, unknown> | null;
  /**
   * Id of the unit the wizard is creating a sub-unit under (#1150). When
   * `null` the wizard is producing a top-level unit (parent = tenant) —
   * the existing behaviour from before #1150. The wizard seeds this slot
   * from the `?parent=<id>` query string when the operator initiates the
   * flow from a unit detail pane's "Create sub-unit" button, and the
   * Identity step also exposes a banner that lets the operator clear it
   * back to top-level. Only the id is persisted; the parent's display
   * name is re-fetched live so a renamed parent reflects on rehydrate.
   */
  parentUnitId: string | null;
}

/**
 * Build the sessionStorage key for a wizard run. Collisions across
 * unrelated keys are avoided by the `spring.wizard.unit-create.` prefix.
 */
export function wizardSessionKey(runId: string): string {
  return `${SESSION_KEY_PREFIX}${runId}`;
}

/**
 * Generate a fresh per-run id. Uses `crypto.randomUUID` when available
 * (browsers + JSDOM ≥ 22) and falls back to a Math.random-based id for
 * the rare environment that lacks it; the id only needs to be unique
 * within sessionStorage of one tab, so the fallback is good enough.
 */
export function generateWizardRunId(): string {
  if (
    typeof crypto !== "undefined" &&
    typeof crypto.randomUUID === "function"
  ) {
    return crypto.randomUUID();
  }
  // RFC 4122 §4.4 v4-like — sufficient for an in-tab key, never used
  // as a security boundary.
  return "wz-" + Math.random().toString(36).slice(2) + Date.now().toString(36);
}

/**
 * Type-guard a parsed JSON blob into a `WizardSnapshot`. Returns `null`
 * for any structural mismatch — the wizard treats a `null` here exactly
 * like a missing slot and starts at step 1. Validation is hand-rolled
 * (no zod) to keep the bundle small and avoid a new dependency for a
 * single schema; the field set is small enough that the explicit checks
 * are easy to follow.
 */
export function validateSnapshot(blob: unknown): WizardSnapshot | null {
  if (blob === null || typeof blob !== "object") return null;
  const candidate = blob as Record<string, unknown>;
  if (candidate.schemaVersion !== WIZARD_STATE_SCHEMA_VERSION) return null;

  const step = candidate.currentStep;
  if (
    typeof step !== "number" ||
    !Number.isInteger(step) ||
    step < 1 ||
    step > 6
  ) {
    return null;
  }

  const formCandidate = candidate.form;
  if (formCandidate === null || typeof formCandidate !== "object") {
    return null;
  }
  const f = formCandidate as Record<string, unknown>;

  const requiredStrings: ReadonlyArray<keyof WizardFormSnapshot> = [
    "name",
    "displayName",
    "description",
    "provider",
    "model",
    "color",
    "tool",
    "hosting",
    "image",
    "runtime",
    "yamlText",
  ];
  for (const key of requiredStrings) {
    if (typeof f[key] !== "string") return null;
  }

  const nullableStrings: ReadonlyArray<keyof WizardFormSnapshot> = [
    "templateId",
    "yamlFileName",
    "connectorSlug",
    "connectorTypeId",
    "parentUnitId",
  ];
  for (const key of nullableStrings) {
    const v = f[key];
    // `parentUnitId` is a #1150 addition; older blobs don't carry it.
    // Treat the missing key as `null` (top-level) so we don't discard
    // an otherwise-valid snapshot just because the operator started the
    // wizard before #1150 shipped.
    if (key === "parentUnitId" && v === undefined) continue;
    if (v !== null && typeof v !== "string") return null;
  }

  if (f.mode !== null && f.mode !== "template" && f.mode !== "scratch" && f.mode !== "yaml") {
    return null;
  }

  if (
    f.connectorConfig !== null &&
    (typeof f.connectorConfig !== "object" || Array.isArray(f.connectorConfig))
  ) {
    return null;
  }

  return {
    schemaVersion: WIZARD_STATE_SCHEMA_VERSION,
    currentStep: step as WizardStep,
    form: {
      name: f.name as string,
      displayName: f.displayName as string,
      description: f.description as string,
      provider: f.provider as string,
      model: f.model as string,
      color: f.color as string,
      tool: f.tool as string,
      hosting: f.hosting as string,
      image: f.image as string,
      runtime: f.runtime as string,
      mode: f.mode as WizardMode | null,
      templateId: f.templateId as string | null,
      yamlText: f.yamlText as string,
      yamlFileName: f.yamlFileName as string | null,
      connectorSlug: f.connectorSlug as string | null,
      connectorTypeId: f.connectorTypeId as string | null,
      connectorConfig: f.connectorConfig as Record<string, unknown> | null,
      parentUnitId:
        f.parentUnitId === undefined
          ? null
          : (f.parentUnitId as string | null),
    },
  };
}

/**
 * Read + validate the snapshot stored under `runId`. Returns `null` for
 * any of: missing key, malformed JSON, stale schema, structural
 * mismatch. Side effect: a malformed slot is left in place — the wizard
 * mounts at step 1 and the next save overwrites it. (Eagerly clearing
 * a malformed slot risks erasing a snapshot a future code version
 * could have read; leaving it lets us evolve the reader without losing
 * data.)
 */
export function loadWizardSnapshot(
  runId: string,
  storage: Storage = sessionStorage,
): WizardSnapshot | null {
  let raw: string | null;
  try {
    raw = storage.getItem(wizardSessionKey(runId));
  } catch {
    // sessionStorage can throw under SecurityError (private mode, COOP).
    // We treat it the same as "no snapshot" — the wizard just starts at
    // step 1 like before #1132.
    return null;
  }
  if (raw === null) return null;
  let parsed: unknown;
  try {
    parsed = JSON.parse(raw);
  } catch {
    return null;
  }
  return validateSnapshot(parsed);
}

/**
 * Serialise + persist a snapshot. Failures are swallowed: a quota
 * exceeded write or a SecurityError shouldn't break the wizard, since
 * the in-memory state remains canonical and the user can still create
 * the unit (they just lose the rehydrate-after-refresh affordance).
 */
export function saveWizardSnapshot(
  runId: string,
  snapshot: WizardSnapshot,
  storage: Storage = sessionStorage,
): void {
  try {
    storage.setItem(wizardSessionKey(runId), JSON.stringify(snapshot));
  } catch {
    // Best-effort; see jsdoc.
  }
}

/**
 * Remove the persisted snapshot for `runId`. Called on successful unit
 * creation and when the operator explicitly cancels/backs out, so the
 * next wizard mount starts clean.
 */
export function clearWizardSnapshot(
  runId: string,
  storage: Storage = sessionStorage,
): void {
  try {
    storage.removeItem(wizardSessionKey(runId));
  } catch {
    // See `saveWizardSnapshot`.
  }
}

/**
 * Read or initialise the current wizard run id for this tab. We persist
 * the id itself under a fixed sessionStorage key so a hard refresh
 * (F5) of /units/create returns to the same run — that is the entire
 * point of #1132. Different tabs see different sessionStorage stores
 * (browser invariant), so two parallel wizard runs can't collide.
 *
 * On any storage failure (private mode, blocked storage), we fall back
 * to a fresh in-memory id; the wizard then operates exactly like the
 * pre-#1132 code (no rehydrate after refresh) without crashing.
 */
export function loadOrInitWizardRunId(
  storage: Storage = sessionStorage,
): string {
  try {
    const existing = storage.getItem(RUN_ID_KEY);
    if (existing && existing.length > 0) {
      return existing;
    }
    const fresh = generateWizardRunId();
    storage.setItem(RUN_ID_KEY, fresh);
    return fresh;
  } catch {
    return generateWizardRunId();
  }
}

/**
 * Clear both the snapshot for `runId` and the tab-level current-run-id
 * pointer. Called when the wizard finishes (successful create) or is
 * explicitly cancelled — the next wizard mount in this tab will mint
 * a fresh run id and start at step 1 with empty fields.
 */
export function clearWizardRun(
  runId: string,
  storage: Storage = sessionStorage,
): void {
  clearWizardSnapshot(runId, storage);
  try {
    storage.removeItem(RUN_ID_KEY);
  } catch {
    // See `saveWizardSnapshot`.
  }
}
