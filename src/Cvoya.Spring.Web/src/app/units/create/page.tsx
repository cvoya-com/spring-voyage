"use client";

import { useCallback, useEffect, useMemo, useRef, useState } from "react";
import { useRouter } from "next/navigation";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import {
  AlertTriangle,
  ArrowLeft,
  Book,
  Check,
  CheckCircle2,
  ChevronDown,
  ChevronRight,
  ExternalLink,
  Eye,
  EyeOff,
  Info,
  KeyRound,
  Package,
  Plug,
  RefreshCw,
  Rocket,
  Search,
  Sparkles,
  Terminal,
  X,
} from "lucide-react";
// Note: unit creation is now driven by the package install API (ADR-0035).
// POST /api/v1/packages/install (catalog) or POST /api/v1/packages/install/file
// (scratch) both return an InstallStatusResponse; the wizard polls
// GET /api/v1/installs/{id} until status reaches "active" or "failed".

import { Breadcrumbs } from "@/components/breadcrumbs";
import { Button } from "@/components/ui/button";
import {
  Card,
  CardContent,
  CardHeader,
  CardTitle,
} from "@/components/ui/card";
import { Input } from "@/components/ui/input";
import { useToast } from "@/components/ui/toast";
import { api } from "@/lib/api/client";
import { getConnectorWizardStep } from "@/connectors/registry";
import {
  useModelProviderModels,
  useModelProviders,
  useConnectorTypes,
  useOllamaModels,
  usePackage,
  usePackages,
  useProviderCredentialStatus,
  useTenantTree,
  useUnit,
  useUnitExecution,
} from "@/lib/api/queries";
import {
  loadImageHistory,
  recordImageReference,
} from "@/lib/image-history";
import { queryKeys } from "@/lib/api/query-keys";
import type { ValidatedTenantTreeNode } from "@/lib/api/validate-tenant-tree";
import type {
  InstalledModelProviderResponse,
  InstallStatusResponse,
  PackageConnectorBindings,
  PackageInputSummary,
  UnitConnectorBindingRequest,
  UnitStatus,
} from "@/lib/api/types";
import {
  DEFAULT_HOSTING_MODE,
  DEFAULT_RUNTIME_ID,
  HOSTING_MODES,
  RUNTIME_LIST,
  RUNTIMES,
  getAllowedProviders,
  getFixedProvider,
  isRuntimeProviderFixed,
  type HostingMode,
  type RuntimeId,
} from "@/lib/ai-models";
import { cn } from "@/lib/utils";
import {
  WIZARD_STATE_SCHEMA_VERSION,
  clearWizardRun,
  loadOrInitWizardRunId,
  loadWizardSnapshot,
  saveWizardSnapshot,
  type WizardFormSnapshot,
  type WizardSnapshot,
  type WizardSource,
  type WizardStep as PersistedWizardStep,
} from "./wizard-persistence";

const DEFAULT_COLOR = "#6366f1";

// #1508: the base/omnibus image that is pre-filled into the image field
// when the wizard first renders. Switching to a runtime replaces it with
// the runtime's own defaultImage (while the field still holds this value).
// Once the operator edits the field or a runtime image has been applied,
// further runtime changes never overwrite the value again.
const BASE_IMAGE = "ghcr.io/cvoya-com/spring-voyage-agents:latest";

// #1132: how long to wait between the last form-state change and the
// sessionStorage write. 300ms is short enough that a refresh-after-fill
// almost always picks up the latest values, while still coalescing the
// per-keystroke storms of typing into the Name / YAML textarea.
const WIZARD_PERSIST_DEBOUNCE_MS = 300;

const NAME_PATTERN = /^[a-z0-9-]+$/;

/**
 * #1150: read the optional `?parent=<unitId>` query param at wizard
 * mount. The "Create sub-unit" button on the unit detail pane
 * navigates here with this param so the wizard knows to attach the
 * new unit as a child of `<unitId>` instead of creating a top-level
 * unit. We read directly off `window.location.search` (lazy
 * `useState` initialiser) to avoid the `Suspense` boundary that
 * `useSearchParams` requires for client components — the wizard is
 * already a `"use client"` page that initialises lots of state from
 * `sessionStorage` the same way. SSR-safe: returns `null` when there
 * is no `window` (server prerender).
 */
function readParentUnitFromUrl(): string | null {
  if (typeof window === "undefined") return null;
  try {
    const params = new URLSearchParams(window.location.search);
    const raw = params.get("parent");
    if (raw === null) return null;
    const trimmed = raw.trim();
    return trimmed.length > 0 ? trimmed : null;
  } catch {
    return null;
  }
}

// How long to wait inside the Validating state before surfacing a
// soft "this is taking longer than expected" notice in the wizard.
// The backend's per-step timeouts (5 min image pull, etc.) are
// authoritative; this number is purely a UX threshold so the operator
// can choose to cancel rather than stare at a spinner. Kept short
// enough to feel responsive but long enough that a normal cold image
// pull on a slow connection won't trip it spuriously.
const VALIDATION_SOFT_TIMEOUT_MS = 60_000;

// ADR-0035 decision 5: wizard now has a Source step (catalog/browse/scratch)
// followed by branch-specific steps. Step count per branch:
//   catalog: Source → Package → Connector → Install (4 steps)
//   browse:  Source → Browse (2 steps, submit disabled)
//   scratch: Source → Identity → Execution → Connector → Install (5 steps)
//
// #1563: the YAML mode is removed entirely; the template mode is
// superseded by catalog.

type Step = 1 | 2 | 3 | 4 | 5;
type Source = WizardSource;

/**
 * Return the max step for the given source branch.
 * - browse: 2 (stub only, submit disabled)
 * - catalog: 4
 * - scratch: 5
 * - null (before source chosen): treated as max 5
 */
function maxStepForSource(source: Source | null): number {
  switch (source) {
    case "browse":
      return 2;
    case "catalog":
      return 4;
    case "scratch":
    default:
      return 5;
  }
}

/**
 * Step label for the current branch. Step 1 is always "Source".
 * The remaining labels are branch-specific.
 */
function stepLabel(source: Source | null, step: Step): string {
  if (step === 1) return "Source";
  switch (source) {
    case "catalog":
      switch (step) {
        case 2: return "Package";
        case 3: return "Connector";
        case 4: return "Install";
        default: return "";
      }
    case "browse":
      return step === 2 ? "Browse" : "";
    case "scratch":
    default:
      switch (step) {
        case 2: return "Identity";
        case 3: return "Execution";
        case 4: return "Connector";
        case 5: return "Install";
        default: return "";
      }
  }
}

// "Where do I get an API key?" deep links live on the wizard because
// the model-provider descriptor's `credentialDisplayHint` is a
// free-text hint — these URLs are the stable landing pages we know
// operators should go to for each backend. The hint renders alongside
// them. Keyed by the canonical provider id ("anthropic" / "openai" /
// "google"); Ollama doesn't surface a "get an API key" path.
const PROVIDER_KEY_HELP: Readonly<
  Record<"anthropic" | "openai" | "google", { href: string; label: string }>
> = {
  anthropic: {
    href: "https://console.anthropic.com/settings/keys",
    label: "Get an Anthropic Console API key",
  },
  openai: {
    href: "https://platform.openai.com/api-keys",
    label: "Get an OpenAI API key",
  },
  google: {
    href: "https://aistudio.google.com/app/apikey",
    label: "Get a Google AI API key",
  },
};

// PendingSecret was used by the old Secrets step (removed in ADR-0035).
// Kept as a type for any future re-introduction but not used currently.

interface FormState {
  // ADR-0035 decision 5: source branch chosen on step 1.
  source: Source | null;
  // Catalog branch: selected package name.
  catalogPackageName: string | null;
  // Catalog branch: operator-supplied input values keyed by input name.
  catalogInputs: Record<string, string>;
  // Scratch branch: identity fields.
  name: string;
  displayName: string;
  description: string;
  // ADR-0038: structured (runtime, modelProviderId, modelId) shape.
  runtime: RuntimeId;
  modelProviderId: string;
  modelId: string;
  color: string;
  hosting: HostingMode;
  // #601: unit-level image default inherited by member agents. Empty
  // string means "don't declare"; the wizard only PUTs through the
  // execution endpoint when filled.
  image: string;
  // Connector binding (#199) — optional, bundled into the create-unit call
  // so the unit and its connector binding are created atomically. `null`
  // connectorSlug means "skip this step". `connectorConfig` is the payload
  // produced by the connector-specific wizard step; it stays `null` until
  // the user fills out enough fields for validity.
  connectorSlug: string | null;
  connectorTypeId: string | null;
  connectorConfig: Record<string, unknown> | null;
  // #626: inline LLM credential entry. Derived from the selected
  // tool+provider at render time (see `deriveRequiredCredentialRuntime`).
  // `credentialKey` is the raw key typed by the operator — it lives in
  // component state just long enough to be POSTed to the server during
  // submission and is never echoed back or persisted client-side. When
  // `saveAsTenantDefault` is true the key is written as a tenant-scoped
  // secret before the unit is created; otherwise it is written as a
  // unit-scoped secret after the unit exists.
  credentialKey: string;
  saveAsTenantDefault: boolean;
  // "override" = the operator clicked the Override link on a
  // tenant-resolvable credential to supply a new value. This is
  // separate from the ON/OFF checkbox so we can tell apart "no override
  // entered" from "override entered but left blank".
  credentialOverrideOpen: boolean;
  // #1150: id of the parent unit this wizard is creating a sub-unit
  // under. `null` keeps the legacy behaviour — the new unit is
  // top-level (parent = tenant). Seeded from the `?parent=<id>` query
  // string when an operator launches the wizard from a unit detail
  // pane's "Create sub-unit" button; the Identity step exposes a
  // banner that lets the operator clear it back to top-level.
  // Kept for backward compat with the URL-param flow; the picker-based
  // flow uses `parentUnitIds` + `parentChoice` (#814).
  parentUnitId: string | null;
  // #814: explicit top-level vs has-parents choice. `null` = not yet
  // chosen (blocks Next on step 1). Seeded to "has-parents" when the
  // `?parent=<id>` URL param is present; otherwise the operator must
  // pick explicitly. This replaces the silent `isTopLevel=true` default.
  parentChoice: "top-level" | "has-parents" | null;
  // #814: the ordered list of parent unit ids when parentChoice is
  // "has-parents". Multi-select; the API accepts an array. Seeded from
  // the URL param on mount.
  parentUnitIds: string[];
}

/**
 * Resolve the installed model-provider entry the wizard needs a
 * credential for, given the structured (runtime, modelProviderId)
 * pair. Returns `null` when:
 *   - no runtime is selected (custom),
 *   - the selected provider isn't installed on the tenant, or
 *   - the provider declares `CredentialKind === "None"` (e.g. Ollama).
 */
function deriveRequiredCredentialProvider(
  runtime: RuntimeId,
  modelProviderId: string,
  providers: InstalledModelProviderResponse[] | null,
): InstalledModelProviderResponse | null {
  if (!providers || providers.length === 0) return null;
  if (runtime === "custom") return null;
  const id = modelProviderId.trim().toLowerCase();
  if (!id) return null;
  const entry = providers.find((p) => p.id.toLowerCase() === id) ?? null;
  if (!entry) return null;
  if (entry.credentialKind === "None") return null;
  return entry;
}

/**
 * #1132: lift a persisted snapshot back into a `FormState`. The
 * snapshot stores `tool` / `hosting` as plain strings (so a future
 * release that adds a new value doesn't have to bump the schema
 * version), so we validate them against the live enum tables here and
 * fall back to the defaults if either one is no longer recognised.
 * Secrets are NOT in `WizardFormSnapshot` to begin with — `INITIAL_FORM`
 * provides empty defaults for those slots.
 */
function mergeSnapshotIntoForm(snap: WizardFormSnapshot): FormState {
  // ADR-0038: validate the persisted runtime against the live catalogue;
  // fall back to the default if the value is no longer recognised (e.g.
  // a snapshot from an older runtime-catalog.yaml release).
  const runtime: RuntimeId = (RUNTIMES[snap.runtime as RuntimeId]
    ? (snap.runtime as RuntimeId)
    : DEFAULT_RUNTIME_ID);
  const hosting: HostingMode = HOSTING_MODES.some((m) => m.id === snap.hosting)
    ? (snap.hosting as HostingMode)
    : DEFAULT_HOSTING_MODE;
  return {
    ...INITIAL_FORM,
    source: snap.source,
    catalogPackageName: snap.catalogPackageName,
    catalogInputs: snap.catalogInputs,
    name: snap.name,
    displayName: snap.displayName,
    description: snap.description,
    runtime,
    modelProviderId: snap.modelProviderId,
    modelId: snap.modelId,
    color: snap.color,
    hosting,
    image: snap.image,
    connectorSlug: snap.connectorSlug,
    connectorTypeId: snap.connectorTypeId,
    connectorConfig: snap.connectorConfig,
    parentUnitId: snap.parentUnitId,
    // #814: rehydrate the new fields; fall back gracefully when the
    // snapshot predates schema v2 (parentChoice / parentUnitIds absent).
    parentChoice: snap.parentChoice ?? (snap.parentUnitId ? "has-parents" : null),
    parentUnitIds: snap.parentUnitIds ?? (snap.parentUnitId ? [snap.parentUnitId] : []),
  };
}

/**
 * #1132: project the live wizard `FormState` down to the
 * persistence-safe snapshot. Anything secret-bearing (raw API key,
 * pending unit-secret values, the override toggles that drive the key
 * input) is dropped — see `WizardFormSnapshot` for the full exclusion
 * list. New persisted fields go here AND in
 * `WizardFormSnapshot` / `validateSnapshot`.
 */
function extractWizardFormSnapshot(form: FormState): WizardFormSnapshot {
  return {
    source: form.source,
    catalogPackageName: form.catalogPackageName,
    catalogInputs: form.catalogInputs,
    name: form.name,
    displayName: form.displayName,
    description: form.description,
    runtime: form.runtime,
    modelProviderId: form.modelProviderId,
    modelId: form.modelId,
    color: form.color,
    hosting: form.hosting,
    image: form.image,
    connectorSlug: form.connectorSlug,
    connectorTypeId: form.connectorTypeId,
    connectorConfig: form.connectorConfig,
    parentUnitId: form.parentUnitId,
    parentChoice: form.parentChoice,
    parentUnitIds: form.parentUnitIds,
  };
}

const INITIAL_FORM: FormState = {
  // ADR-0035 decision 5: source not yet chosen.
  source: null,
  catalogPackageName: null,
  catalogInputs: {},
  name: "",
  displayName: "",
  description: "",
  // ADR-0038: structured (runtime, model.provider, model.id) shape.
  // The provider picker is shown only when the runtime is multi-provider
  // (`spring-voyage`); fixed-provider runtimes (`claude-code`, `codex`,
  // `gemini`) seed `modelProviderId` lazily from the runtime catalogue.
  runtime: DEFAULT_RUNTIME_ID,
  modelProviderId: getFixedProvider(DEFAULT_RUNTIME_ID) ?? "",
  modelId: "",
  color: DEFAULT_COLOR,
  hosting: DEFAULT_HOSTING_MODE,
  // #1508: pre-fill the base image so the field is never blank.
  image: BASE_IMAGE,
  connectorSlug: null,
  connectorTypeId: null,
  connectorConfig: null,
  credentialKey: "",
  saveAsTenantDefault: false,
  credentialOverrideOpen: false,
  parentUnitId: null,
  // #814: explicit parent-choice toggle and multi-select ids.
  parentChoice: null,
  parentUnitIds: [],
};

/**
 * Wizard progress rail — v2 reskin (SURF-reskin-create-flows, #859).
 * Styled as a sticky chip-row matching the Explorer's tab bar: brand
 * tint on the active step, filled dot on completed steps, muted pill
 * on the remaining ones. Each step advertises its state via
 * `data-step-state` so tests can key off the new markup without
 * snapshotting the exact class string.
 *
 * ADR-0035 decision 5: step labels are branch-specific; `source` drives
 * which labels render and how many steps appear in the rail.
 */
function StepIndicator({
  current,
  source,
}: {
  current: Step;
  source: Source | null;
}) {
  const max = maxStepForSource(source);
  const steps = Array.from({ length: max }, (_, i) => (i + 1) as Step);
  return (
    <nav
      aria-label="Create unit progress"
      className="sticky top-0 z-10 -mx-4 md:-mx-6 border-b border-border bg-background/85 px-4 py-3 backdrop-blur md:px-6"
    >
      <ol className="flex items-center gap-2 overflow-x-auto">
        {steps.map((n, idx) => {
          const done = n < current;
          const active = n === current;
          const state = done ? "done" : active ? "active" : "upcoming";
          const label = stepLabel(source, n);
          return (
            <li
              key={n}
              data-step={n}
              data-step-state={state}
              aria-current={active ? "step" : undefined}
              className="flex items-center gap-2 whitespace-nowrap"
            >
              <span
                className={cn(
                  "flex h-6 w-6 items-center justify-center rounded-full text-[11px] font-semibold transition-colors",
                  done && "bg-primary text-primary-foreground",
                  active &&
                    "border border-primary bg-primary/10 text-primary",
                  !done && !active && "bg-muted text-muted-foreground",
                )}
              >
                {done ? <Check className="h-3.5 w-3.5" aria-hidden /> : n}
              </span>
              {label && (
                <span
                  className={cn(
                    "text-sm",
                    active
                      ? "font-medium text-foreground"
                      : done
                        ? "text-foreground/80"
                        : "text-muted-foreground",
                  )}
                >
                  {label}
                </span>
              )}
              {idx < steps.length - 1 && (
                <span className="mx-1 h-px w-6 bg-border" aria-hidden />
              )}
            </li>
          );
        })}
      </ol>
    </nav>
  );
}

export default function CreateUnitPage() {
  const router = useRouter();
  const { toast } = useToast();
  const queryClient = useQueryClient();

  // #1132: per-tab wizard run id + initial rehydrate. Both initialisers
  // run exactly once thanks to React's lazy useState semantics, so we
  // don't introduce a useEffect-driven flash of the empty step-1 form
  // before the snapshot lands. SSR-safe: the helpers no-op when
  // sessionStorage is unavailable, in which case the wizard behaves
  // exactly like the pre-#1132 code (fresh state every mount).
  const [runId] = useState<string>(() => {
    if (typeof window === "undefined") return "";
    return loadOrInitWizardRunId();
  });
  const initialSnapshot = useMemo<WizardSnapshot | null>(() => {
    if (typeof window === "undefined" || runId === "") return null;
    return loadWizardSnapshot(runId);
  }, [runId]);

  const [step, setStep] = useState<Step>(
    (initialSnapshot?.currentStep as Step | undefined) ?? 1,
  );
  // #1150: the `?parent=<id>` query param wins over a rehydrated
  // snapshot, because the operator just clicked "Create sub-unit" on
  // a specific parent and that intent is more recent than any
  // sessionStorage blob. When the URL omits `parent` we fall back to
  // the snapshot's own `parentUnitId` (could itself be `null` for
  // top-level), so a hard refresh of `/units/create?parent=foo` keeps
  // the parent context, and a refresh of plain `/units/create`
  // preserves whatever parent the operator was last editing.
  const initialParentFromUrl = useMemo(readParentUnitFromUrl, []);
  const [form, setForm] = useState<FormState>(() => {
    const base = initialSnapshot
      ? mergeSnapshotIntoForm(initialSnapshot.form)
      : INITIAL_FORM;
    if (initialParentFromUrl !== null) {
      // URL param seeds the parent-choice state so the picker reflects the
      // "Create sub-unit" intent (#814). Also preserves backward compat with
      // `parentUnitId` for tests / existing banner logic.
      return {
        ...base,
        parentUnitId: initialParentFromUrl,
        parentChoice: "has-parents" as const,
        parentUnitIds: [initialParentFromUrl],
      };
    }
    return base;
  });
  // #1508: track whether the image field is still at the base-image
  // placeholder ("base") or has been overwritten by a runtime pick or
  // a manual edit ("applied"). Once "applied", runtime changes never
  // touch the field again.
  const [imageSource, setImageSource] = useState<"base" | "applied">(() => {
    // Rehydrate: if the snapshot's image differs from BASE_IMAGE the
    // operator had already applied a value in a previous session.
    const snap = initialSnapshot?.form;
    if (snap && snap.image && snap.image !== BASE_IMAGE) return "applied";
    return "base";
  });
  const [stepError, setStepError] = useState<string | null>(null);
  const [submitError, setSubmitError] = useState<string | null>(null);
  const [submitWarnings, setSubmitWarnings] = useState<string[]>([]);
  // ADR-0035 install flow. After the install POST returns we store the
  // installId and poll GET /api/v1/installs/{id} every 2 s until
  // status reaches "active" (success) or "failed". On success we
  // redirect to the Explorer; on failure we show retry/abort.
  const [installId, setInstallId] = useState<string | null>(null);
  // Snapshot of the last polled status for rendering the Install step.
  const [installStatus, setInstallStatus] = useState<InstallStatusResponse | null>(null);
  // Post-create validation phase. When a unit is created successfully
  // we transition the Finalize step into a Validating view that
  // POST /start's the unit, polls until terminal, then either
  // redirects to the Explorer (`Running` / `Stopped`) or keeps the
  // operator on the step with a Back affordance (`Error`). Mirrors the
  // CLI's `spring unit create --wait` default; the CLI contract is
  // documented in the wizard copy. Tracked by #983 / #980.
  const [createdUnitName, setCreatedUnitName] = useState<string | null>(null);
  const [startError, setStartError] = useState<string | null>(null);
  const [startRequested, setStartRequested] = useState(false);
  // Soft client-side timeout for the Validating phase. The backend has
  // its own per-step timeouts (5 min for the image pull), but those are
  // long enough that a stuck workflow looks indistinguishable from "still
  // working" to a user staring at the wizard. After
  // VALIDATION_SOFT_TIMEOUT_MS we surface a "this is taking longer than
  // expected" notice with Cancel / Retry affordances. The validation
  // itself keeps running in the background; the notice is purely UX.
  const [validationStartedAt, setValidationStartedAt] = useState<number | null>(
    null,
  );
  const [validationSoftTimedOut, setValidationSoftTimedOut] = useState(false);

  // #1132: debounced sessionStorage save. Stops once the unit is
  // created (we don't want to overwrite the snapshot — it's about to be
  // cleared by the success path; persisting in the meantime would race
  // with `clearWizardRun` and either resurrect a stale blob or write a
  // mid-mutation form state). Saves a stable, secrets-free subset (see
  // `WizardFormSnapshot`) under the per-tab run id.
  const persistDebounceRef = useRef<ReturnType<typeof setTimeout> | null>(
    null,
  );
  useEffect(() => {
    if (runId === "") return;
    // Don't persist during or after install — the snapshot is about to
    // be cleared on success, and persisting mid-install would resurrect
    // a stale blob if the user refreshes during polling.
    if (installId !== null) return;
    if (createdUnitName !== null) return;
    if (persistDebounceRef.current !== null) {
      clearTimeout(persistDebounceRef.current);
    }
    persistDebounceRef.current = setTimeout(() => {
      const snapshot: WizardSnapshot = {
        schemaVersion: WIZARD_STATE_SCHEMA_VERSION,
        currentStep: step as PersistedWizardStep,
        form: extractWizardFormSnapshot(form),
      };
      saveWizardSnapshot(runId, snapshot);
    }, WIZARD_PERSIST_DEBOUNCE_MS);
    return () => {
      if (persistDebounceRef.current !== null) {
        clearTimeout(persistDebounceRef.current);
        persistDebounceRef.current = null;
      }
    };
  }, [runId, step, form, createdUnitName, installId]);

  // #1150: when the wizard is creating a sub-unit we fetch the parent
  // unit envelope so the Identity step can show the operator which
  // unit they're nesting under (display name, falling back to the
  // address). Driven by parentUnitIds[0] when #814 picker is in use;
  // falls back to parentUnitId for the legacy URL-param-only path.
  // The query is cached behind the standard `units.detail` key, so a
  // back-button → forward to the detail pane immediately picks up the
  // cached envelope without a round-trip.
  const firstParentId =
    form.parentChoice === "has-parents" && form.parentUnitIds.length > 0
      ? form.parentUnitIds[0]
      : form.parentUnitId;
  const parentUnitQuery = useUnit(firstParentId ?? "", {
    enabled: firstParentId !== null,
  });
  const parentUnitName = useMemo<string | null>(() => {
    if (firstParentId === null) return null;
    const data = parentUnitQuery.data;
    if (!data) return firstParentId;
    return data.displayName?.trim() || data.name || firstParentId;
  }, [firstParentId, parentUnitQuery.data]);
  const parentUnitMissing =
    firstParentId !== null &&
    parentUnitQuery.isError;

  // #814: tenant tree for the parent-unit picker. Fetched only when
  // step 1 is active and the operator has not yet chosen "top-level",
  // so we defer the request until the picker is likely to be shown.
  const tenantTreeQuery = useTenantTree({ enabled: step === 1 });
  // Flatten the tree into a list of units (kind === "Unit") for the
  // picker. The tenant root node itself is filtered out. Agent leaf
  // nodes are also excluded — only organisational units are valid
  // parents, matching the server-side validation.
  const availableParentUnits = useMemo<
    Array<{ id: string; name: string; displayName: string }>
  >(() => {
    if (!tenantTreeQuery.data) return [];
    const result: Array<{ id: string; name: string; displayName: string }> = [];
    function walk(node: ValidatedTenantTreeNode) {
      // The root node has kind "Tenant" — skip it but walk its children.
      if (node.kind === "Unit") {
        result.push({
          id: node.id,
          name: node.name,
          displayName: node.desc ? `${node.name} — ${node.desc}` : node.name,
        });
      }
      if (node.children) {
        for (const child of node.children) walk(child);
      }
    }
    walk(tenantTreeQuery.data);
    return result;
  }, [tenantTreeQuery.data]);

  // #622 / #968: recently-used image references. Loaded once on mount
  // from localStorage; the list grows when the wizard successfully
  // creates a unit with a non-blank image.
  const [imageHistory, setImageHistory] = useState<string[]>(() =>
    loadImageHistory(),
  );

  // ADR-0035 decision 5: catalog source — list of installed packages.
  // Fetched once per session (cached). The catalog step renders a
  // package picker from this list. `usePackages` queries
  // GET /api/v1/packages which is always available regardless of
  // whether any packages are installed.
  const packagesQuery = usePackages();
  const packages = packagesQuery.data ?? null;
  const packagesLoading = packagesQuery.isPending;
  const packagesError = packagesQuery.isError
    ? packagesQuery.error instanceof Error
      ? packagesQuery.error.message
      : String(packagesQuery.error)
    : null;

  // #1615: detail for the currently-selected catalog package — surfaces
  // the input schema so the package step can render a typed form field
  // per declared input. Only fetches once a package is picked; the
  // dashboard's tanstack-query cache dedupes against the detail page.
  const selectedPackageQuery = usePackage(form.catalogPackageName ?? "", {
    enabled: form.source === "catalog" && form.catalogPackageName !== null,
  });
  // ADR-0037 D2: package-level `inputs:` is removed; connector-binding
  // parameters now live on per-artefact `requires:`. The wizard's
  // input-rendering path is dead under the new shape — keep the empty
  // list so the existing form-validation loop still typechecks while
  // #1727 deletes the wizard's input UI sections wholesale.
  const selectedPackageInputs = useMemo<PackageInputSummary[]>(
    () => [],
    [],
  );
  // Reference selectedPackageQuery to keep the existing fetch path live —
  // the wizard still needs the package detail for connector declarations
  // and the package-name display even though it no longer reads inputs.
  void selectedPackageQuery;
  const selectedPackageLoading =
    form.source === "catalog" &&
    form.catalogPackageName !== null &&
    selectedPackageQuery.isPending;
  const selectedPackageError =
    selectedPackageQuery.isError && form.catalogPackageName !== null
      ? selectedPackageQuery.error instanceof Error
        ? selectedPackageQuery.error.message
        : String(selectedPackageQuery.error)
      : null;

  // #1672: the legacy `githubPrefill` shim has been removed. The catalog
  // package now declares its connector dependency through the manifest's
  // `connectors:` block (#1670) and the install request carries the
  // operator-supplied binding through `connectorBindings.package.<slug>`
  // — see `buildConnectorBindings()` and the install mutation below.
  // Connector outputs (owner, repo, installation-id) no longer flow
  // through the package-inputs map; an OSS package whose member units
  // used to ask the operator to retype `${{ inputs.github_* }}` is
  // expected to migrate to the `connectors:` declaration instead.

  // #1615: which declared required inputs are still unsatisfied? An
  // input is satisfied when it has a non-empty value OR the package
  // declares a default (the install pipeline applies the default at
  // Phase 1). The Next gate consults this list; the package step
  // surfaces a per-field hint when one of these is empty.
  const missingRequiredCatalogInputs = useMemo<string[]>(() => {
    if (form.source !== "catalog" || form.catalogPackageName === null) {
      return [];
    }
    const missing: string[] = [];
    for (const def of selectedPackageInputs) {
      if (!def.required) continue;
      const name = def.name;
      if (!name) continue;
      const value = form.catalogInputs[name];
      if (typeof value === "string" && value.trim() !== "") continue;
      if (
        typeof def.default === "string" &&
        def.default !== null &&
        def.default !== undefined &&
        def.default !== ""
      ) {
        continue;
      }
      missing.push(name);
    }
    return missing;
  }, [
    form.source,
    form.catalogPackageName,
    form.catalogInputs,
    selectedPackageInputs,
  ]);

  // Connector catalog (#199): fetched once so the Connector screen can
  // render the picker without waiting on the server for each render.
  const connectorTypesQuery = useConnectorTypes();
  const connectorTypes = connectorTypesQuery.data ?? null;
  const connectorTypesError = connectorTypesQuery.isError
    ? connectorTypesQuery.error instanceof Error
      ? connectorTypesQuery.error.message
      : String(connectorTypesQuery.error)
    : null;

  // ADR-0038: model providers installed on the current tenant. Feeds
  // the conditional provider picker (multi-provider runtimes) and
  // surfaces credential/model metadata consumed by the Execution step.
  const modelProvidersQuery = useModelProviders();
  const installedProviders = useMemo<InstalledModelProviderResponse[]>(
    () => modelProvidersQuery.data ?? [],
    [modelProvidersQuery.data],
  );

  // ADR-0038 §1: the runtime descriptor decides whether the provider is
  // fixed or operator-picked. `effectiveProviderId` resolves the
  // currently-active model provider id for the runtime — fixed when
  // the runtime is single-provider, falling back to whatever the
  // operator selected (or first installed) when multi-provider.
  const runtimeDescriptor = RUNTIMES[form.runtime];
  const fixedProviderId = getFixedProvider(form.runtime);

  // The provider picker is rendered only when the runtime allows the
  // operator to choose. The set of options is the intersection of the
  // runtime's `allowedProviders` and the tenant-installed providers.
  const pickerProviders = useMemo<InstalledModelProviderResponse[]>(() => {
    if (runtimeDescriptor.isProviderFixed) return [];
    const allowed = getAllowedProviders(form.runtime) ?? [];
    return installedProviders.filter((p) =>
      (allowed as readonly string[]).includes(p.id),
    );
  }, [installedProviders, form.runtime, runtimeDescriptor.isProviderFixed]);

  // #350: Ollama is the multi-provider runtime's "no-credential" path —
  // we still ask the live server for its model list because it surfaces
  // richer per-model metadata than the catalogue.
  const ollamaEnabled = form.modelProviderId === "ollama";
  const ollamaQuery = useOllamaModels({ enabled: ollamaEnabled });
  const ollamaModels = ollamaQuery.data?.map((m) => m.name) ?? null;
  const ollamaModelsLoading = ollamaEnabled && ollamaQuery.isPending;

  // ADR-0038: per-provider model catalogue. The wizard reads it
  // whenever the active provider id is non-empty AND installed on the
  // tenant. The Ollama branch keeps its dedicated hook above.
  const activeProviderId = form.modelProviderId.trim().toLowerCase();
  const activeProviderInstalled = useMemo<boolean>(() => {
    if (!activeProviderId) return false;
    return installedProviders.some(
      (p) => p.id.toLowerCase() === activeProviderId,
    );
  }, [activeProviderId, installedProviders]);
  const modelProviderModelsQuery = useModelProviderModels(activeProviderId, {
    enabled:
      activeProviderId !== "" &&
      activeProviderId !== "ollama" &&
      activeProviderInstalled,
  });
  const providerModels =
    modelProviderModelsQuery.data?.map((m) => m.id) ?? null;

  // ADR-0038: when the runtime fixes its provider, snap the
  // modelProviderId to the fixed value. When multi-provider, default to
  // the first installed entry that matches the runtime's allow-list.
  // The setForm calls are guarded so they only fire when the value
  // actually needs to change (no infinite render loop).
  const effectiveProvider = useMemo(() => {
    if (fixedProviderId !== null) return fixedProviderId;
    if (form.modelProviderId !== "") return form.modelProviderId;
    return pickerProviders.length > 0 ? pickerProviders[0].id : "";
  }, [fixedProviderId, form.modelProviderId, pickerProviders]);
  if (effectiveProvider !== form.modelProviderId) {
    setForm((prev) =>
      prev.modelProviderId === effectiveProvider
        ? prev
        : { ...prev, modelProviderId: effectiveProvider },
    );
  }

  // ADR-0038: snap the model id to the first available entry whenever
  // the current value is no longer in the active provider's catalogue.
  // The `custom` runtime keeps the operator-typed value as-is — there
  // is no catalogue to validate against.
  const effectiveModel = useMemo(() => {
    if (form.runtime === "custom") return form.modelId;
    if (form.modelProviderId === "ollama") {
      if (!ollamaModels || ollamaModels.length === 0) return form.modelId;
      if (ollamaModels.includes(form.modelId)) return form.modelId;
      return ollamaModels[0];
    }
    if (!providerModels || providerModels.length === 0) return form.modelId;
    if (providerModels.includes(form.modelId)) return form.modelId;
    return providerModels[0];
  }, [
    form.runtime,
    form.modelProviderId,
    form.modelId,
    providerModels,
    ollamaModels,
  ]);
  if (effectiveModel !== form.modelId) {
    setForm((prev) =>
      prev.modelId === effectiveModel
        ? prev
        : { ...prev, modelId: effectiveModel },
    );
  }

  // ADR-0038: derive the installed model-provider entry the wizard
  // needs a credential for. Returns `null` when no credential is
  // required (custom runtime, uninstalled provider, or
  // `credentialKind === "None"` — Ollama).
  const requiredCredentialProviderEntry = useMemo(
    () =>
      deriveRequiredCredentialProvider(
        form.runtime,
        form.modelProviderId,
        installedProviders.length > 0 ? installedProviders : null,
      ),
    [form.runtime, form.modelProviderId, installedProviders],
  );
  // The credential UI only knows about the closed set of key-bearing
  // providers. Ollama (credentialKind === "None") is filtered upstream
  // by `deriveRequiredCredentialProvider`; anything else outside this
  // set carries no key entry and we surface no credential prompt.
  const requiredCredentialProvider: KeyedProviderId | null = (() => {
    const id = requiredCredentialProviderEntry?.id;
    if (id === "anthropic" || id === "openai" || id === "google") return id;
    return null;
  })();

  // Status probe runs whenever a provider needs a key. For the
  // ollama case it still runs so the existing reachability banner
  // stays visible; when the derivation returns null (custom runtime)
  // the query is disabled entirely.
  const credentialProbeProvider =
    requiredCredentialProvider ??
    // Multi-provider runtime + ollama still gets the reachability probe.
    (form.modelProviderId === "ollama" ? "ollama" : null);
  const credentialStatusQuery = useProviderCredentialStatus(
    credentialProbeProvider ?? "",
    {
      enabled: credentialProbeProvider !== null,
      // #1397: pass the chosen agent image so the server can reference it in
      // the format-rejected suggestion text (e.g. "the image X uses the Claude
      // Code path which requires an OAuth token, not an API key"). Only pass
      // when the operator has explicitly entered an image; omit for Ollama
      // (no credential format mismatch possible) and when no image is typed.
      agentImage:
        credentialProbeProvider !== null &&
        credentialProbeProvider !== "ollama" &&
        form.image.trim().length > 0
          ? form.image.trim()
          : undefined,
    },
  );
  const credentialStatus = credentialStatusQuery.data ?? null;

  // T-07 (#949): host-side credential validation in the wizard is gone.
  // POST /api/v1/units returns 201 immediately and the backend workflow
  // drives validation. The wizard only persists the key; the detail
  // page's Validation panel reports whether the backend accepted it.
  // The Model dropdown renders against the per-provider catalogue so
  // Next is never gated on a live reach-out to the LLM.
  const isOllamaDapr = form.modelProviderId === "ollama";
  const activeModelList: readonly string[] | null = useMemo(() => {
    if (isOllamaDapr) return ollamaModels;
    if (providerModels && providerModels.length > 0) return providerModels;
    return null;
  }, [isOllamaDapr, ollamaModels, providerModels]);
  const showModelDropdown = isOllamaDapr || activeModelList !== null;

  // Issue #661: Next on the Execution screen is disabled until a model
  // is selected from the catalog. `form.runtime === "custom"` is
  // handled as a separate escape hatch by `canGoNext` / `validateStep3`;
  // they bypass this check entirely.
  const modelIsSelected =
    activeModelList !== null &&
    form.modelId.trim().length > 0 &&
    activeModelList.includes(form.modelId);

  const update = <K extends keyof FormState>(key: K, value: FormState[K]) => {
    setForm((prev) => ({ ...prev, [key]: value }));
  };

  const validateStep1 = (): string | null => {
    // Step 1 is always Source selection.
    if (form.source === null) return "Choose a source to continue.";
    return null;
  };

  // Step 2 validation depends on source branch.
  const validateStep2 = (): string | null => {
    if (form.source === "browse") {
      // Browse is a stub — no validation, submit is always disabled.
      return null;
    }
    if (form.source === "catalog") {
      // Catalog: require a package selection.
      if (!form.catalogPackageName) return "Select a package to continue.";
      // #1615: every required input declared by the package must have a
      // value (or a default) before Next is allowed. Without this gate
      // the install lands a 400 "Input '<name>' is required" at Phase 1
      // — surfacing that as an in-form error is the whole point of
      // shipping the schema-aware UI.
      const missing = missingRequiredCatalogInputs;
      if (missing.length > 0) {
        const labels = missing.map((n) => `"${n}"`).join(", ");
        return `Fill in ${labels} to continue.`;
      }
      return null;
    }
    // Scratch branch: step 2 is Identity.
    if (!form.name.trim()) return "Name is required.";
    if (!NAME_PATTERN.test(form.name))
      return "Name must be URL-safe (lowercase letters, digits, and hyphens).";
    // #814: require an explicit parent-unit choice. The silent
    // `isTopLevel=true` default is gone — the operator must decide.
    if (form.parentChoice === null) {
      return "Choose whether this unit is top-level or has parent units.";
    }
    if (
      form.parentChoice === "has-parents" &&
      form.parentUnitIds.length === 0
    ) {
      return "Select at least one parent unit.";
    }
    return null;
  };

  // Step 3 validation depends on source branch.
  const validateStep3 = (): string | null => {
    if (form.source === "catalog") {
      // Catalog step 3 is Connector — skip is always valid.
      if (form.connectorSlug !== null && form.connectorConfig === null) {
        return "Finish filling the connector configuration, or choose Skip.";
      }
      return null;
    }
    // Scratch branch: step 3 is Execution.
    // #1508: image is required — the pre-fill ensures this is never blank
    // in normal flow, but enforce defensively so a stale snapshot cannot
    // produce a unit that immediately fails PullingImage validation.
    if (!form.image.trim()) {
      return "A container image is required. Set one in the Execution environment section.";
    }
    // Issue #661: the Execution screen requires a selected model whenever
    // the tool has a known catalog. `modelIsSelected` covers the happy
    // path; the one branch it doesn't cover is "tool=custom" (no catalog
    // at all — skip the check) and "spring-voyage + ollama still loading"
    // (the list is empty, so the user cannot pick anything yet).
    if (form.runtime === "custom") return null;
    if (isOllamaDapr && ollamaModelsLoading) {
      return "Wait for the Ollama model list to load before continuing.";
    }
    if (!modelIsSelected) {
      return "Select a model to continue.";
    }
    return null;
  };

  // Step 4 validation depends on source branch.
  const validateStep4 = (): string | null => {
    if (form.source === "catalog") {
      // Catalog step 4 is Install — no pre-submit validation here.
      return null;
    }
    // Scratch branch: step 4 is Connector.
    // Skip is always allowed. If the user picked a connector, the wizard
    // step component must have produced a non-null config (it pushes null
    // while the form is incomplete, so this gate catches the "selected
    // but unfilled" case).
    if (form.connectorSlug !== null && form.connectorConfig === null) {
      return "Finish filling the connector configuration, or choose Skip.";
    }
    return null;
  };

  const handleNext = () => {
    setStepError(null);
    if (step === 1) {
      const err = validateStep1();
      if (err) {
        setStepError(err);
        return;
      }
    }
    if (step === 2) {
      // T-07 (#949): no host-side credential validation here — the
      // backend validates during `Validating`. We only require a
      // selected model (or a custom tool, which skips the gate).
      const err = validateStep2();
      if (err) {
        setStepError(err);
        return;
      }
    }
    if (step === 3) {
      const err = validateStep3();
      if (err) {
        setStepError(err);
        return;
      }
    }
    if (step === 4) {
      const err = validateStep4();
      if (err) {
        setStepError(err);
        return;
      }
    }
    const max = maxStepForSource(form.source);
    if (step < max) setStep((s) => (s + 1) as Step);
  };

  const handleBack = () => {
    setStepError(null);
    setSubmitError(null);
    if (step > 1) setStep((s) => (s - 1) as Step);
  };

  // #1132: explicit cancel — clear the persisted wizard snapshot for
  // this run AND route back to the units list. Without the explicit
  // clear, the next visit to /units/create in this tab would resume
  // the abandoned form, which is the opposite of what "Cancel" means.
  // We also cancel any pending debounced save so it cannot resurrect
  // the freshly-cleared blob between this click and the actual page
  // unmount (router.push is async).
  const handleCancel = () => {
    if (persistDebounceRef.current !== null) {
      clearTimeout(persistDebounceRef.current);
      persistDebounceRef.current = null;
    }
    if (runId !== "") {
      clearWizardRun(runId);
    }
    router.push("/units");
  };

  /**
   * Builds a CreateUnitRequest body from the wizard form state.
   * Used by the scratch branch (the catalog branch posts directly to
   * the package install pipeline). Image/runtime are not part of
   * CreateUnitRequest — they are persisted in a follow-up
   * setUnitExecution PUT call after the unit row is created.
   *
   * ADR-0035 designates the package install pipeline as the long-term
   * home for both wizard branches, but the v0.1 PackageManifest schema
   * (kind: UnitPackage with `unit:` as a string ref) does not support
   * an inline unit definition. Until the schema and resolver gain
   * inline-artefact support, the scratch branch falls back to the
   * direct unit-creation endpoint that the wizard used pre-#1563.
   */
  const buildScratchCreateRequest = () => {
    // ADR-0038: `CreateUnitRequest` no longer carries `tool` or
    // `provider` slots — runtime + model.provider land via the
    // dedicated `setUnitExecution` PUT after creation. The flat
    // `model` field on the create request is preserved as a free-text
    // model id for backwards compatibility within v0.1; the structured
    // execution block carries the authoritative `{provider, id}`.
    const provider = form.modelProviderId.trim() || undefined;
    const model = form.modelId.trim() || undefined;
    const hosting =
      form.hosting !== DEFAULT_HOSTING_MODE ? form.hosting : undefined;
    const color = form.color.trim() || undefined;
    const displayName = form.displayName.trim() || form.name.trim();
    const description = form.description.trim();
    const connector = buildConnectorBinding() ?? undefined;

    const isTopLevel = form.parentChoice === "top-level" ? true : undefined;
    const parentUnitIds =
      form.parentChoice === "has-parents" && form.parentUnitIds.length > 0
        ? form.parentUnitIds
        : undefined;

    // Helper return shape: keep `provider` / `model` on the local
    // record so the downstream `setUnitExecution` call can read both,
    // even though the create-unit endpoint doesn't accept `provider`.
    return {
      // Wire payload — only fields `CreateUnitRequest` accepts.
      wire: {
        name: form.name.trim(),
        displayName,
        description,
        model,
        color,
        hosting,
        connector,
        parentUnitIds,
        isTopLevel,
      },
      // Side-band: surfaced to setUnitExecution to populate
      // `execution.model.provider` and `execution.model.id`.
      provider,
      model,
    };
  };

  // #1672: project the connector wizard's output into the package-install
  // `connectorBindings` shape. We only emit a `package.<slug>` entry today
  // — per-unit overrides land via the same channel when the package's
  // manifest declares `inherit: false` on member units (followed-up by
  // the planned multi-step wizard for package-level overrides). Returns
  // `undefined` so the field is omitted from the request body when no
  // connector is configured.
  const buildCatalogConnectorBindings = ():
    | PackageConnectorBindings
    | undefined => {
    if (!form.connectorSlug || form.connectorConfig === null) {
      return undefined;
    }
    return {
      package: {
        [form.connectorSlug]: {
          config: form.connectorConfig,
        },
      },
      // Per-unit overrides are not yet surfaced in the wizard for v0.1; the
      // OSS package post-migration relies on package-level inheritance for
      // every member unit.
      units: null,
    };
  };

  // Build the connector-binding payload the server expects. Returns `null`
  // when the user skipped the Connector step OR filled it out partially
  // (the wizard-step component pushes `null` up until the form is valid).
  // The server is strict: either the binding is absent, or it's
  // well-formed.
  const buildConnectorBinding = (): UnitConnectorBindingRequest | null => {
    if (!form.connectorSlug || form.connectorConfig === null) {
      return null;
    }
    return {
      // typeId is required on the wire; pass the zero GUID when we only
      // have the slug (the server accepts that as a lookup fallback).
      typeId:
        form.connectorTypeId ?? "00000000-0000-0000-0000-000000000000",
      typeSlug: form.connectorSlug,
      config: form.connectorConfig,
    };
  };

  // Install mutation. Routes by source branch:
  //   catalog → POST /api/v1/packages/install (JSON body) — ADR-0035 path
  //   scratch → POST /api/v1/tenant/units (+ PUT /execution for image/runtime)
  //
  // The scratch path returns a synthesised InstallStatusResponse with
  // status="active" so the polling/redirect code below can stay
  // shared. ADR-0035 anticipates a single install pipeline; the v0.1
  // PackageManifest schema does not yet support inline unit
  // definitions, so the wizard's scratch path stays on the direct
  // unit-create endpoint until the schema catches up.
  const installMutation = useMutation({
    mutationFn: async (): Promise<InstallStatusResponse> => {
      if (form.source === "catalog") {
        if (!form.catalogPackageName) {
          throw new Error("No package selected.");
        }
        // #1672: attach the connector-binding payload sourced from the
        // forced connector step. Empty when the package declares no
        // required connectors. The wire shape is `connectorBindings`
        // — package-scope inheritance lands every member unit on the
        // same binding unless the unit's manifest opts out.
        const bindings = buildCatalogConnectorBindings();
        return api.installPackages([
          {
            packageName: form.catalogPackageName,
            inputs: form.catalogInputs,
            connectorBindings: bindings,
          },
        ]);
      }

      // Scratch branch: create the unit, then write image/runtime via
      // the dedicated execution endpoint (CreateUnitRequest does not
      // accept those two fields).
      const req = buildScratchCreateRequest();
      const created = await api.createUnit(req.wire);
      const image = form.image.trim();
      if (image) {
        try {
          // ADR-0038 (PR-1b): the wire field renamed `agent` →
          // `runtime` (operator-chosen runtime id), the legacy flat
          // `provider` slot is gone, and `model` is now structured
          // `{provider, id}`. We only emit a model object when both
          // provider and id are resolvable; otherwise it stays null
          // and the dispatcher falls through to defaults.
          const wireProvider = req.provider ?? null;
          const wireModelId = req.model ?? null;
          await api.setUnitExecution(created.name, {
            image: image || null,
            containerRuntime: null,
            runtime: form.runtime !== "custom" ? form.runtime : null,
            model:
              wireProvider && wireModelId
                ? { provider: wireProvider, id: wireModelId }
                : null,
          });
        } catch (e) {
          // Best-effort: surface the error but the unit row already
          // exists, so the operator can re-edit execution from the
          // unit page if this fails.
          throw e instanceof Error
            ? new Error(`Unit created but execution update failed: ${e.message}`)
            : e;
        }
      }

      const now = new Date().toISOString();
      const synthesised: InstallStatusResponse = {
        installId: `scratch:${created.name}`,
        status: "active",
        packages: [
          { packageName: created.name, state: "active", errorMessage: null },
        ],
        startedAt: now,
        completedAt: now,
        error: null,
      };
      return synthesised;
    },
    onMutate: () => {
      setSubmitError(null);
      setSubmitWarnings([]);
      setInstallId(null);
      setInstallStatus(null);
    },
    onSuccess: (resp) => {
      setInstallId(resp.installId);
      setInstallStatus(resp);
      // Clear the wizard snapshot — the install exists; rehydrating
      // it would put the operator in "install the same package again".
      if (persistDebounceRef.current !== null) {
        clearTimeout(persistDebounceRef.current);
        persistDebounceRef.current = null;
      }
      if (runId !== "") {
        clearWizardRun(runId);
      }
      // Record image history for scratch path.
      if (form.source === "scratch") {
        const submittedImage = form.image.trim();
        if (submittedImage) {
          recordImageReference(submittedImage);
          setImageHistory(loadImageHistory());
        }
      }
    },
    onError: (err) => {
      const message = err instanceof Error ? err.message : String(err);
      setSubmitError(message);
      toast({
        title: "Install failed",
        description: message,
        variant: "destructive",
      });
    },
  });

  // Poll install status every 2 s while a real (catalog-pipeline)
  // install is in flight. Scratch-branch ids are synthetic
  // (`scratch:<unit-name>`) and the install endpoint cannot resolve
  // them — the synthesised "active" response is the source of truth
  // for those.
  const isSyntheticInstallId =
    installId !== null && installId.startsWith("scratch:");
  const installStatusQuery = useQuery({
    queryKey: queryKeys.installs.detail(installId ?? ""),
    queryFn: () => api.getInstallStatus(installId!),
    enabled: installId !== null && !isSyntheticInstallId,
    refetchInterval: (query) => {
      const data = query.state.data as InstallStatusResponse | undefined;
      if (!data) return 2000;
      // Stop polling once the install reaches a terminal state.
      if (data.status === "active" || data.status === "failed") return false;
      return 2000;
    },
  });
  // Keep installStatus in sync with the latest poll result.
  const latestInstallStatus = installStatusQuery.data ?? installStatus;
  const installActive = latestInstallStatus?.status === "active";
  const installFailed = latestInstallStatus?.status === "failed";
  const installPending = installId !== null && !installActive && !installFailed;

  // On install success, invalidate unit/dashboard caches and redirect.
  useEffect(() => {
    if (!installActive) return;
    queryClient.invalidateQueries({ queryKey: queryKeys.units.all });
    queryClient.invalidateQueries({ queryKey: queryKeys.dashboard.all });
    queryClient.invalidateQueries({ queryKey: queryKeys.tenant.tree() });
    toast({ title: "Install complete" });
    router.push("/units");
  // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [installActive]);

  // Retry install: POST /api/v1/installs/{id}/retry.
  const retryInstallMutation = useMutation({
    mutationFn: () => api.retryInstall(installId!),
    onSuccess: (resp) => {
      setInstallId(resp.installId);
      setInstallStatus(resp);
    },
    onError: (err) => {
      const message = err instanceof Error ? err.message : String(err);
      toast({
        title: "Retry failed",
        description: message,
        variant: "destructive",
      });
    },
  });

  // Abort install: POST /api/v1/installs/{id}/abort.
  const abortInstallMutation = useMutation({
    mutationFn: () => api.abortInstall(installId!),
    onSuccess: () => {
      setInstallId(null);
      setInstallStatus(null);
      queryClient.invalidateQueries({ queryKey: queryKeys.units.all });
      queryClient.invalidateQueries({ queryKey: queryKeys.dashboard.all });
      toast({ title: "Install aborted" });
      router.push("/units");
    },
    onError: (err) => {
      const message = err instanceof Error ? err.message : String(err);
      toast({
        title: "Abort failed",
        description: message,
        variant: "destructive",
      });
    },
  });

  // Keep the old unit-polling infrastructure for the scratch path's
  // post-install unit state (scratch installs create a named unit that
  // we can navigate to). The validation panel still uses it.
  const createdUnitQuery = useUnit(createdUnitName ?? "", {
    enabled: createdUnitName !== null,
    refetchInterval: 1000,
  });
  const createdUnit = createdUnitQuery.data ?? null;
  const createdUnitExecution = useUnitExecution(createdUnitName ?? "", {
    enabled: createdUnitName !== null,
  });
  const createdStatus: UnitStatus | null = createdUnit?.status ?? null;
  const isTerminalSuccess =
    createdStatus === "Running" || createdStatus === "Stopped";
  const isTerminalError = createdStatus === "Error";
  const isValidating = createdUnitName !== null && !isTerminalSuccess && !isTerminalError;

  useEffect(() => {
    if (createdUnitName && isTerminalSuccess) {
      if (persistDebounceRef.current !== null) {
        clearTimeout(persistDebounceRef.current);
        persistDebounceRef.current = null;
      }
      if (runId !== "") {
        clearWizardRun(runId);
      }
      router.push(
        `/units?node=${encodeURIComponent(createdUnitName)}&tab=Overview`,
      );
    }
  }, [createdUnitName, isTerminalSuccess, router, runId]);

  useEffect(() => {
    if (!isValidating) {
      setValidationStartedAt(null);
      setValidationSoftTimedOut(false);
      return;
    }
    if (validationStartedAt === null) {
      setValidationStartedAt(Date.now());
      setValidationSoftTimedOut(false);
      return;
    }
    if (validationSoftTimedOut) return;
    const elapsed = Date.now() - validationStartedAt;
    const remaining = Math.max(0, VALIDATION_SOFT_TIMEOUT_MS - elapsed);
    if (remaining === 0) {
      setValidationSoftTimedOut(true);
      return;
    }
    const handle = window.setTimeout(() => {
      setValidationSoftTimedOut(true);
    }, remaining);
    return () => window.clearTimeout(handle);
  }, [isValidating, validationStartedAt, validationSoftTimedOut]);

  const cancelMutation = useMutation({
    mutationFn: async (name: string) => {
      await api.deleteUnit(name);
    },
    onSuccess: (_data, name) => {
      queryClient.invalidateQueries({ queryKey: queryKeys.units.all });
      queryClient.invalidateQueries({ queryKey: queryKeys.dashboard.all });
      toast({ title: "Unit cancelled", description: `Removed ${name}.` });
      router.push("/units");
    },
    onError: (err) => {
      const message = err instanceof Error ? err.message : String(err);
      toast({
        title: "Failed to cancel unit",
        description: message,
        variant: "destructive",
      });
    },
  });

  const handleCancelCreatedUnit = () => {
    if (!createdUnitName) return;
    cancelMutation.mutate(createdUnitName);
  };

  const deleteAndGoBackMutation = useMutation({
    mutationFn: async (name: string) => {
      await api.deleteUnit(name);
    },
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: queryKeys.units.all });
      queryClient.invalidateQueries({ queryKey: queryKeys.dashboard.all });
      queryClient.invalidateQueries({ queryKey: queryKeys.tenant.tree() });
      setCreatedUnitName(null);
      setStartRequested(false);
      setStartError(null);
      setSubmitError(null);
      setSubmitWarnings([]);
      setValidationStartedAt(null);
      setValidationSoftTimedOut(false);
      setStep(2);
    },
    onError: (err) => {
      const message = err instanceof Error ? err.message : String(err);
      toast({
        title: "Failed to remove the failed unit",
        description: message,
        variant: "destructive",
      });
    },
  });

  const revalidateMutation = useMutation({
    mutationFn: (name: string) => api.revalidateUnit(name),
    onSuccess: () => {
      if (createdUnitName) {
        queryClient.invalidateQueries({
          queryKey: queryKeys.units.detail(createdUnitName),
        });
      }
      setValidationStartedAt(null);
      setValidationSoftTimedOut(false);
    },
    onError: (err) => {
      const message = err instanceof Error ? err.message : String(err);
      toast({
        title: "Failed to retry validation",
        description: message,
        variant: "destructive",
      });
    },
  });

  const handleRetry = () => {
    if (!createdUnitName) return;
    revalidateMutation.mutate(createdUnitName);
  };

  const submitting = installMutation.isPending;

  // #626: gate the Create button when the selected tool requires an
  // LLM credential AND no key has been typed AND the probe says
  // nothing is resolvable at tenant/unit scope. "Nothing resolvable"
  // is the only state that actually blocks dispatch — when a tenant
  // default exists we let the operator proceed without supplying a
  // key, because the unit resolves from tenant at dispatch time.
  const missingCredential = useMemo(() => {
    if (requiredCredentialProvider === null) return false;
    if (form.credentialKey.trim().length > 0) return false;
    // Still loading the probe — don't block; the probe is best-effort
    // and we don't want a flaky network to jam the wizard.
    if (credentialStatusQuery.isPending) return false;
    if (credentialStatusQuery.isError) return false;
    // Resolvable at unit/tenant scope = dispatch will succeed without
    // a freshly-typed key.
    if (credentialStatus?.resolvable === true) return false;
    return true;
  }, [
    requiredCredentialProvider,
    form.credentialKey,
    credentialStatus?.resolvable,
    credentialStatusQuery.isPending,
    credentialStatusQuery.isError,
  ]);

  const missingCredentialMessage =
    requiredCredentialProvider !== null
      ? `Set the ${providerLabel(requiredCredentialProvider)} API key to continue.`
      : null;

  // handleCreate is no longer used — install is triggered by the
  // Install button's onClick which calls installMutation.mutate() directly.

  // Stable handler passed to each connector wizard-step component. The
  // component fires it whenever its local form produces a valid payload
  // (or `null` when incomplete). We memoise so the component doesn't
  // see a new reference on every re-render of the wizard.
  const handleConnectorConfigChange = useCallback(
    (config: Record<string, unknown> | null) => {
      setForm((prev) =>
        prev.connectorConfig === config ? prev : { ...prev, connectorConfig: config },
      );
    },
    [],
  );

  const canGoNext = useMemo(() => {
    if (step === 1) {
      // Source step — require a selection.
      return form.source !== null;
    }
    if (step === 2) {
      if (form.source === "browse") {
        // Browse stub: Next advances to the only sub-step but submit is
        // always disabled on that sub-step. Allow advancement here
        // (there is nothing to validate on step 2 for browse).
        return true;
      }
      if (form.source === "catalog") {
        if (form.catalogPackageName === null) return false;
        // #1615: every required input declared by the package must
        // carry a value (or have a default) before Next is allowed.
        return missingRequiredCatalogInputs.length === 0;
      }
      // Scratch step 2 = Identity. Require name + parentChoice.
      if (!form.name.trim()) return false;
      if (!NAME_PATTERN.test(form.name)) return false;
      if (form.parentChoice === null) return false;
      if (
        form.parentChoice === "has-parents" &&
        form.parentUnitIds.length === 0
      )
        return false;
      return true;
    }
    if (step === 3) {
      if (form.source === "catalog") {
        // Catalog step 3 = Connector — skip always allowed.
        if (form.connectorSlug === null) return true;
        return form.connectorConfig !== null;
      }
      // Scratch step 3 = Execution.
      if (!form.image.trim()) return false;
      if (form.runtime === "custom") return true;
      if (isOllamaDapr && ollamaModelsLoading) return false;
      return modelIsSelected;
    }
    if (step === 4) {
      if (form.source === "catalog") {
        // Catalog step 4 = Install — no advance (it's the last step,
        // canGoNext here is used for the Next button visibility).
        return false;
      }
      // Scratch step 4 = Connector — skip always allowed.
      if (form.connectorSlug === null) return true;
      return form.connectorConfig !== null;
    }
    // Step 5 is Install (scratch) — no advance.
    return false;
  }, [
    step,
    form,
    isOllamaDapr,
    ollamaModelsLoading,
    modelIsSelected,
    missingRequiredCatalogInputs,
  ]);

  // Issue #927-followup (post-T-07): explain *why* Next is disabled on
  // Step 2. Without this hint the wizard can dead-end silently — the
  // Model dropdown only renders when the agent-runtimes catalog returns
  // a matching runtime, so an unreachable platform API or an
  // uninstalled runtime collapses the model surface and leaves the
  // operator staring at a disabled button with no way to diagnose. We
  // surface the most specific actionable reason, in priority order,
  // mirroring the gates `canGoNext` / `validateStep2` consult.
  // nextDisabledReason is only relevant for the Execution step in the
  // scratch branch (step 3 when source === "scratch"). For all other
  // steps the disabled state is self-evident from the form fields.
  const nextDisabledReason = useMemo<string | null>(() => {
    if (form.source !== "scratch" || step !== 3) return null;
    if (canGoNext) return null;
    if (form.runtime === "custom") return null;
    if (modelProvidersQuery.isPending) {
      return "Loading the model-provider catalogue…";
    }
    if (modelProvidersQuery.isError) {
      return "Could not load the model-provider catalogue.";
    }
    const runtimeLabel = runtimeDescriptor.displayName;
    if (installedProviders.length === 0) {
      return "No configured model providers.";
    }
    if (
      !runtimeDescriptor.isProviderFixed &&
      form.modelProviderId.trim() === ""
    ) {
      return `Pick a model provider for the ${runtimeLabel} runtime.`;
    }
    if (
      runtimeDescriptor.isProviderFixed &&
      requiredCredentialProviderEntry === null &&
      form.modelProviderId !== "ollama"
    ) {
      return `The "${runtimeLabel}" runtime requires the ${form.modelProviderId} provider, which is not installed on this server. Install the matching model provider, or pick a different runtime.`;
    }
    if (isOllamaDapr && ollamaModelsLoading) {
      return "Loading the model list from the Ollama server…";
    }
    if (!showModelDropdown) {
      return "No model catalog is available yet — wait for the catalog to load, or pick a different runtime.";
    }
    if (!modelIsSelected) {
      return "Select a model from the dropdown to continue.";
    }
    return null;
  }, [
    form.source,
    step,
    canGoNext,
    form.runtime,
    form.modelProviderId,
    runtimeDescriptor,
    modelProvidersQuery.isPending,
    modelProvidersQuery.isError,
    installedProviders.length,
    requiredCredentialProviderEntry,
    isOllamaDapr,
    ollamaModelsLoading,
    showModelDropdown,
    modelIsSelected,
  ]);

  // Truthy when the model-provider catalogue itself is the cause of an
  // empty Step 3 (no providers / fetch failure for a non-custom runtime).
  // Drives the in-card banner above the form so the operator sees the
  // root cause, not just the "Next is disabled" symptom underneath.
  // The fetch-error message is intentionally short — the platform API
  // is supposed to always be reachable from the dashboard host.
  const modelProviderCatalogIssue = useMemo<string | null>(() => {
    if (form.source !== "scratch") return null;
    if (form.runtime === "custom") return null;
    if (modelProvidersQuery.isPending) return null;
    if (modelProvidersQuery.isError) {
      return "Could not load the model-provider catalogue.";
    }
    if (installedProviders.length === 0) {
      return "No configured model providers.";
    }
    return null;
  }, [
    form.source,
    form.runtime,
    modelProvidersQuery.isPending,
    modelProvidersQuery.isError,
    installedProviders.length,
  ]);

  return (
    <div className="space-y-6">
      <Breadcrumbs
        items={[
          { label: "Units", href: "/units" },
          { label: "Create" },
        ]}
      />

      <div className="flex flex-col gap-1">
        <div className="flex items-center gap-2">
          <Rocket className="h-5 w-5 text-primary" aria-hidden="true" />
          <h1 className="text-2xl font-bold">Install a unit</h1>
        </div>
        <p className="text-sm text-muted-foreground">
          {false ? (
            <>
              Register a new unit nested under an existing parent. Mirrors{" "}
              <code className="rounded bg-muted px-1 py-0.5 font-mono text-xs">
                spring unit create --parent-unit
              </code>
              .
            </>
          ) : (
            <>
              Install a unit from a catalog package or build one from scratch. Mirrors{" "}
              <code className="rounded bg-muted px-1 py-0.5 font-mono text-xs">
                spring package install
              </code>
              .
            </>
          )}
        </p>
      </div>

      <StepIndicator current={step} source={form.source} />

      {/* Step 1: Source selection (ADR-0035 decision 5) */}
      {step === 1 && (
        <Card>
          <CardHeader>
            <CardTitle>Choose a source</CardTitle>
          </CardHeader>
          <CardContent className="space-y-3">
            <SourceCard
              icon={<Book className="h-5 w-5" />}
              title="Catalog"
              description="Install a pre-built unit from a package in the catalog. The package supplies the full definition; you only need to provide any required inputs."
              selected={form.source === "catalog"}
              onSelect={() => update("source", "catalog")}
              testId="source-card-catalog"
            />

            <SourceCard
              icon={<Search className="h-5 w-5" />}
              title="Browse"
              description="Search the Spring Voyage package registry for community packages. (Coming soon — use the CLI for now.)"
              selected={form.source === "browse"}
              onSelect={() => update("source", "browse")}
              testId="source-card-browse"
            />

            <SourceCard
              icon={<Sparkles className="h-5 w-5" />}
              title="Scratch"
              description="Define a new unit from scratch. You supply the name, execution tool, model, and optional connector binding."
              selected={form.source === "scratch"}
              onSelect={() => update("source", "scratch")}
              testId="source-card-scratch"
            />

            {stepError && (
              <p className="rounded-md border border-destructive/50 bg-destructive/10 px-3 py-2 text-sm text-destructive">
                {stepError}
              </p>
            )}
          </CardContent>
        </Card>
      )}

      {/* Step 2: branch-specific */}
      {/* Step 2 browse: Coming Soon stub */}
      {step === 2 && form.source === "browse" && (
        <Card>
          <CardHeader>
            <CardTitle className="flex items-center gap-2">
              <Search className="h-5 w-5" /> Browse packages
            </CardTitle>
          </CardHeader>
          <CardContent className="space-y-4">
            <div
              data-testid="browse-coming-soon"
              className="rounded-md border border-border bg-muted/30 px-4 py-6 text-center space-y-3"
            >
              <Package
                className="mx-auto h-8 w-8 text-muted-foreground"
                aria-hidden
              />
              <p className="text-sm font-medium">Coming soon</p>
              <p className="text-xs text-muted-foreground">
                The package registry browser is not yet available in the
                portal. Use the CLI to search and install packages:
              </p>
              <code className="block rounded bg-muted px-3 py-2 font-mono text-xs text-muted-foreground">
                spring package install &lt;package-name&gt;
              </code>
            </div>
          </CardContent>
        </Card>
      )}

      {/* Step 2 catalog: Package picker */}
      {step === 2 && form.source === "catalog" && (
        <Card>
          <CardHeader>
            <CardTitle className="flex items-center gap-2">
              <Book className="h-5 w-5" /> Select a package
            </CardTitle>
          </CardHeader>
          <CardContent className="space-y-4">
            {packagesLoading && (
              <p className="text-xs text-muted-foreground">
                Loading catalog…
              </p>
            )}
            {packagesError && (
              <p className="rounded-md border border-destructive/50 bg-destructive/10 px-3 py-2 text-sm text-destructive">
                Failed to load catalog: {packagesError}
              </p>
            )}
            {!packagesLoading && packages && packages.length === 0 && (
              <p className="text-xs text-muted-foreground">
                No packages are installed on this tenant. Install a package
                first using the CLI:
                <code className="ml-1 rounded bg-muted px-1 py-0.5 font-mono">
                  spring package install &lt;name&gt;
                </code>
              </p>
            )}
            {packages && packages.length > 0 && (
              <ul className="space-y-2">
                {packages.map((pkg) => {
                  const isSelected = form.catalogPackageName === pkg.name;
                  return (
                    <li key={pkg.name}>
                      <button
                        type="button"
                        data-testid={`package-option-${pkg.name}`}
                        aria-pressed={isSelected}
                        onClick={() =>
                          setForm((prev) => ({
                            ...prev,
                            catalogPackageName: pkg.name,
                            catalogInputs: {},
                          }))
                        }
                        className={cn(
                          "flex w-full items-start gap-3 rounded-md border p-3 text-left text-sm transition-colors",
                          isSelected
                            ? "border-primary bg-primary/5"
                            : "border-border hover:bg-accent/50",
                        )}
                      >
                        <span
                          className={cn(
                            "mt-0.5 flex h-4 w-4 shrink-0 items-center justify-center rounded-full border",
                            isSelected
                              ? "border-primary bg-primary"
                              : "border-border",
                          )}
                        >
                          {isSelected && (
                            <span className="block h-2 w-2 rounded-full bg-white" />
                          )}
                        </span>
                        <span className="flex-1">
                          <span className="font-medium font-mono">
                            {pkg.name}
                          </span>
                          {pkg.description && (
                            <span className="block text-xs text-muted-foreground">
                              {pkg.description}
                            </span>
                          )}
                          <span className="block text-[11px] text-muted-foreground mt-0.5">
                            {pkg.unitTemplateCount} unit{pkg.unitTemplateCount !== 1 ? "s" : ""}
                            {pkg.agentTemplateCount > 0 && `, ${pkg.agentTemplateCount} agent${pkg.agentTemplateCount !== 1 ? "s" : ""}`}
                            {pkg.skillCount > 0 && `, ${pkg.skillCount} skill${pkg.skillCount !== 1 ? "s" : ""}`}
                          </span>
                        </span>
                      </button>
                    </li>
                  );
                })}
              </ul>
            )}

            {/* #1615: render one form field per declared package input.
                The package's `inputs:` block lives on PackageDetail; the
                wizard step pre-fills GitHub-connector keys from the
                connector wizard step (when configured) but the operator
                can override every field. Required inputs without a value
                AND no default block Next via `missingRequiredCatalogInputs`. */}
            {form.catalogPackageName && (
              <CatalogInputsPanel
                packageName={form.catalogPackageName}
                inputs={selectedPackageInputs}
                values={form.catalogInputs}
                onChange={(name, value) =>
                  setForm((prev) => ({
                    ...prev,
                    catalogInputs: { ...prev.catalogInputs, [name]: value },
                  }))
                }
                loading={selectedPackageLoading}
                error={selectedPackageError}
                missingRequired={missingRequiredCatalogInputs}
              />
            )}

            {stepError && (
              <p className="rounded-md border border-destructive/50 bg-destructive/10 px-3 py-2 text-sm text-destructive">
                {stepError}
              </p>
            )}
          </CardContent>
        </Card>
      )}

      {/* Step 2 scratch: Identity */}
      {step === 2 && form.source === "scratch" && (
        <Card>
          <CardHeader>
            <CardTitle>Identity</CardTitle>
          </CardHeader>
          <CardContent className="space-y-4">
            <label className="block space-y-1">
              <span className="text-sm text-muted-foreground">
                Name<span className="text-destructive"> *</span>
              </span>
              <Input
                value={form.name}
                onChange={(e) => update("name", e.target.value)}
                placeholder="engineering-team"
                autoFocus
              />
              <span className="block text-xs text-muted-foreground">
                Lowercase letters, digits, and hyphens only. This becomes
                the unit&apos;s address (e.g. <code>my-unit</code>).
              </span>
            </label>

            <label className="block space-y-1">
              <span className="text-sm text-muted-foreground">
                Display name
              </span>
              <Input
                value={form.displayName}
                onChange={(e) => update("displayName", e.target.value)}
                placeholder="Engineering Team"
              />
            </label>

            <label className="block space-y-1">
              <span className="text-sm text-muted-foreground">Description</span>
              <Input
                value={form.description}
                onChange={(e) => update("description", e.target.value)}
                placeholder="Ships the core product."
              />
            </label>

            <div className="grid grid-cols-1 gap-4 sm:grid-cols-2">
              <label className="block space-y-1">
                <span className="text-sm text-muted-foreground">Color</span>
                <div className="flex items-center gap-2">
                  <input
                    type="color"
                    value={form.color}
                    onChange={(e) => update("color", e.target.value)}
                    className="h-9 w-12 cursor-pointer rounded border border-input bg-background p-1"
                    aria-label="Pick color"
                  />
                  <Input
                    value={form.color}
                    onChange={(e) => update("color", e.target.value)}
                    placeholder={DEFAULT_COLOR}
                  />
                </div>
              </label>
            </div>

            {/* #814: parent-unit picker */}
            <div className="space-y-3 border-t border-border pt-4">
              <div>
                <h3 className="text-sm font-semibold">
                  Parent<span className="text-destructive"> *</span>
                </h3>
                <p className="text-xs text-muted-foreground">
                  Where does this unit live in the tenant hierarchy?
                </p>
              </div>

              <div
                role="group"
                aria-label="Parent unit choice"
                className="flex flex-wrap gap-2"
              >
                <button
                  type="button"
                  data-testid="parent-choice-top-level"
                  role="radio"
                  aria-checked={form.parentChoice === "top-level"}
                  onClick={() =>
                    setForm((prev) => ({
                      ...prev,
                      parentChoice: "top-level",
                      parentUnitIds: [],
                      parentUnitId: null,
                    }))
                  }
                  className={cn(
                    "flex items-center gap-1.5 rounded-md border px-3 py-1.5 text-sm transition-colors",
                    form.parentChoice === "top-level"
                      ? "border-primary bg-primary/10 text-primary"
                      : "border-border text-muted-foreground hover:bg-accent/50",
                  )}
                >
                  Top-level (tenant root)
                </button>
                <button
                  type="button"
                  data-testid="parent-choice-has-parents"
                  role="radio"
                  aria-checked={form.parentChoice === "has-parents"}
                  onClick={() =>
                    setForm((prev) => ({
                      ...prev,
                      parentChoice: "has-parents",
                    }))
                  }
                  className={cn(
                    "flex items-center gap-1.5 rounded-md border px-3 py-1.5 text-sm transition-colors",
                    form.parentChoice === "has-parents"
                      ? "border-primary bg-primary/10 text-primary"
                      : "border-border text-muted-foreground hover:bg-accent/50",
                  )}
                >
                  Has parent units
                </button>
              </div>

              {form.parentChoice === "has-parents" && (
                <div className="space-y-2" data-testid="parent-unit-picker">
                  {tenantTreeQuery.isPending && (
                    <p className="text-xs text-muted-foreground">
                      Loading units…
                    </p>
                  )}
                  {tenantTreeQuery.isError && (
                    <p className="text-xs text-destructive">
                      Could not load the unit list.
                    </p>
                  )}
                  {!tenantTreeQuery.isPending &&
                    availableParentUnits.length === 0 &&
                    !tenantTreeQuery.isError && (
                      <p className="text-xs text-muted-foreground">
                        No existing units found in this tenant.
                      </p>
                    )}
                  {availableParentUnits.length > 0 && (
                    <div className="space-y-1.5 max-h-48 overflow-y-auto rounded-md border border-border bg-muted/20 p-2">
                      {availableParentUnits.map((u) => {
                        const isSelected = form.parentUnitIds.includes(u.id);
                        return (
                          <button
                            key={u.id}
                            type="button"
                            data-testid={`parent-option-${u.id}`}
                            aria-pressed={isSelected}
                            onClick={() => {
                              setForm((prev) => {
                                const already = prev.parentUnitIds.includes(u.id);
                                const next = already
                                  ? prev.parentUnitIds.filter((id) => id !== u.id)
                                  : [...prev.parentUnitIds, u.id];
                                return {
                                  ...prev,
                                  parentUnitIds: next,
                                  parentUnitId: next[0] ?? null,
                                };
                              });
                            }}
                            className={cn(
                              "flex w-full items-center gap-2 rounded px-2 py-1.5 text-left text-sm transition-colors",
                              isSelected
                                ? "bg-primary/10 text-primary"
                                : "text-foreground hover:bg-accent/50",
                            )}
                          >
                            <span
                              className={cn(
                                "flex h-4 w-4 shrink-0 items-center justify-center rounded border text-[10px]",
                                isSelected
                                  ? "border-primary bg-primary text-primary-foreground"
                                  : "border-border",
                              )}
                              aria-hidden
                            >
                              {isSelected && (
                                <Check className="h-3 w-3" aria-hidden />
                              )}
                            </span>
                            <span className="flex-1 font-mono text-xs">
                              {u.name}
                            </span>
                          </button>
                        );
                      })}
                    </div>
                  )}
                  {parentUnitMissing && firstParentId && (
                    <div
                      role="status"
                      data-testid="parent-unit-banner"
                      data-parent-id={firstParentId}
                      className="flex flex-wrap items-start gap-2 rounded-md border border-warning/50 bg-warning/15 px-3 py-2 text-sm text-foreground"
                    >
                      <AlertTriangle
                        className="mt-0.5 h-4 w-4 shrink-0 text-warning"
                        aria-hidden
                      />
                      <p className="flex-1">
                        Could not load the parent unit{" "}
                        <code className="rounded bg-muted px-1 py-0.5 font-mono text-xs">
                          {firstParentId}
                        </code>
                        .
                      </p>
                      <button
                        type="button"
                        data-testid="parent-unit-clear"
                        onClick={() =>
                          setForm((prev) => ({
                            ...prev,
                            parentUnitId: null,
                            parentUnitIds: [],
                            parentChoice: "top-level",
                          }))
                        }
                        className="text-xs font-medium underline underline-offset-2 text-foreground/80 hover:text-foreground"
                      >
                        Clear
                      </button>
                    </div>
                  )}
                  {!parentUnitMissing && form.parentUnitIds.length > 0 && (
                    <p className="text-xs text-muted-foreground">
                      <Sparkles
                        className="mr-1 inline h-3 w-3 text-primary"
                        aria-hidden
                      />
                      Sub-unit of{" "}
                      <strong className="font-semibold">
                        {form.parentUnitIds.length === 1
                          ? (parentUnitName ?? form.parentUnitIds[0])
                          : `${form.parentUnitIds.length} units`}
                      </strong>
                      .
                    </p>
                  )}
                </div>
              )}
            </div>

            {stepError && (
              <p className="rounded-md border border-destructive/50 bg-destructive/10 px-3 py-2 text-sm text-destructive">
                {stepError}
              </p>
            )}
          </CardContent>
        </Card>
      )}

      {/* Step 3: Connector for catalog, Execution for scratch */}
      {step === 3 && form.source === "catalog" && (
        <Card>
          <CardHeader>
            <CardTitle className="flex items-center gap-2">
              <Plug className="h-5 w-5" /> Connector
            </CardTitle>
          </CardHeader>
          <CardContent className="space-y-4 text-sm">
            <p className="text-muted-foreground">
              Optionally bind this package install to a connector. Skip to
              configure a connector later from the unit&apos;s Connector tab.
            </p>
            {connectorTypesError && (
              <p className="rounded-md border border-destructive/50 bg-destructive/10 px-3 py-2 text-sm text-destructive">
                Failed to load connectors: {connectorTypesError}
              </p>
            )}
            <div className="space-y-2">
              <label className="flex cursor-pointer items-start gap-3 rounded-md border border-border p-3">
                <input
                  type="radio"
                  name="connector-choice"
                  checked={form.connectorSlug === null}
                  onChange={() =>
                    setForm((prev) => ({
                      ...prev,
                      connectorSlug: null,
                      connectorTypeId: null,
                      connectorConfig: null,
                    }))
                  }
                  className="mt-1"
                />
                <span>
                  <span className="font-medium">Skip</span>
                  <span className="block text-xs text-muted-foreground">
                    Install without a connector binding.
                  </span>
                </span>
              </label>
              {connectorTypes?.map((c) => {
                const isSelected = form.connectorSlug === c.typeSlug;
                const WizardStep = getConnectorWizardStep(c.typeSlug);
                return (
                  <label
                    key={c.typeId}
                    className={cn(
                      "block space-y-2 rounded-md border p-3 transition-colors",
                      isSelected ? "border-primary bg-primary/5" : "border-border",
                    )}
                  >
                    <span className="flex cursor-pointer items-start gap-3">
                      <input
                        type="radio"
                        name="connector-choice"
                        checked={isSelected}
                        onChange={() =>
                          setForm((prev) => ({
                            ...prev,
                            connectorSlug: c.typeSlug,
                            connectorTypeId: c.typeId,
                            connectorConfig: null,
                          }))
                        }
                        className="mt-1"
                      />
                      <span className="flex-1">
                        <span className="font-medium">{c.displayName}</span>
                        <span className="block text-xs text-muted-foreground">
                          {c.description}
                        </span>
                      </span>
                    </span>
                    {isSelected && WizardStep && (
                      <WizardStep
                        onChange={handleConnectorConfigChange}
                        initialValue={form.connectorConfig}
                      />
                    )}
                    {isSelected && !WizardStep && (
                      <div className="rounded-md border border-amber-500/50 bg-amber-500/10 px-3 py-2 text-xs text-amber-900 dark:text-amber-200">
                        This connector doesn&apos;t ship a wizard UI. Select{" "}
                        <strong>Skip</strong> and configure it from the
                        unit&apos;s Connector tab after creation.
                      </div>
                    )}
                  </label>
                );
              })}
              {connectorTypes && connectorTypes.length === 0 && (
                <p className="rounded-md border border-border bg-muted/40 px-3 py-2 text-xs text-muted-foreground">
                  No connectors are registered on this server.
                </p>
              )}
            </div>
            {stepError && (
              <p className="rounded-md border border-destructive/50 bg-destructive/10 px-3 py-2 text-sm text-destructive">
                {stepError}
              </p>
            )}
          </CardContent>
        </Card>
      )}

      {/* Step 3 scratch: Execution */}
      {step === 3 && form.source === "scratch" && (
        <Card>
          <CardHeader>
            <CardTitle>Agent Runtime &amp; Model</CardTitle>
          </CardHeader>
          <CardContent className="space-y-4">
            {modelProviderCatalogIssue && (
              <div
                role="alert"
                data-testid="model-provider-catalog-issue"
                className="flex items-start gap-2 rounded-md border border-warning/50 bg-warning/15 px-3 py-2 text-sm text-foreground"
              >
                <AlertTriangle className="mt-0.5 h-4 w-4 shrink-0" aria-hidden />
                <p className="flex-1">{modelProviderCatalogIssue}</p>
              </div>
            )}
            <label className="block space-y-1">
              <span className="text-sm text-muted-foreground">Agent Runtime</span>
              <select
                value={form.runtime}
                onChange={(e) => {
                  const nextRuntime = e.target.value as RuntimeId;
                  const nextFixed = getFixedProvider(nextRuntime);
                  setForm((prev) => ({
                    ...prev,
                    runtime: nextRuntime,
                    // ADR-0038: snap the provider id when the new
                    // runtime fixes its provider; otherwise keep the
                    // operator's choice (effectiveProvider snaps it
                    // back into the allow-list on the next render).
                    modelProviderId: nextFixed ?? prev.modelProviderId,
                    modelId: "",
                  }));
                }}
                aria-label="Agent Runtime"
                data-testid="unit-create-runtime-select"
                className="flex h-9 w-full rounded-md border border-input bg-background px-3 py-1 text-sm shadow-sm focus-visible:outline-none focus-visible:ring-1 focus-visible:ring-ring disabled:cursor-not-allowed disabled:opacity-50"
              >
                {RUNTIME_LIST.map((r) => (
                  <option key={r.id} value={r.id}>{r.displayName}</option>
                ))}
              </select>
            </label>
            {!runtimeDescriptor.isProviderFixed && (
              <label className="block space-y-1">
                <span className="text-sm text-muted-foreground">Model Provider</span>
                <select
                  value={form.modelProviderId}
                  onChange={(e) => {
                    const nextProvider = e.target.value;
                    setForm((prev) => ({
                      ...prev,
                      modelProviderId: nextProvider,
                      modelId: "",
                    }));
                  }}
                  aria-label="Model provider"
                  data-testid="unit-create-model-provider-select"
                  disabled={pickerProviders.length === 0}
                  className="flex h-9 w-full rounded-md border border-input bg-background px-3 py-1 text-sm shadow-sm focus-visible:outline-none focus-visible:ring-1 focus-visible:ring-ring disabled:cursor-not-allowed disabled:opacity-50"
                >
                  {pickerProviders.map((p) => (
                    <option key={p.id} value={p.id}>{p.displayName}</option>
                  ))}
                </select>
              </label>
            )}
            {requiredCredentialProvider !== null && (
              <CredentialSection
                requiredProvider={requiredCredentialProvider}
                status={credentialStatus}
                statusPending={credentialStatusQuery.isPending}
                statusError={credentialStatusQuery.isError}
                credentialKey={form.credentialKey}
                saveAsTenantDefault={form.saveAsTenantDefault}
                overrideOpen={form.credentialOverrideOpen}
                ollamaProbe={null}
                onKeyChange={(v) => update("credentialKey", v)}
                onToggleSaveAsTenantDefault={(v) => update("saveAsTenantDefault", v)}
                onToggleOverride={(v) => {
                  setForm((prev) => ({
                    ...prev,
                    credentialOverrideOpen: v,
                    credentialKey: v ? prev.credentialKey : "",
                    saveAsTenantDefault: v ? prev.saveAsTenantDefault : false,
                  }));
                }}
              />
            )}
            {form.modelProviderId === "ollama" && credentialStatus && (
              <OllamaReachabilityBanner data={credentialStatus} />
            )}
            {showModelDropdown && (
              <label className="block space-y-1">
                <span className="text-sm text-muted-foreground">Model</span>
                <select
                  value={form.modelId}
                  onChange={(e) => update("modelId", e.target.value)}
                  aria-label="Model"
                  data-testid="unit-create-model-select"
                  disabled={isOllamaDapr && ollamaModelsLoading}
                  className="flex h-9 w-full rounded-md border border-input bg-background px-3 py-1 text-sm shadow-sm focus-visible:outline-none focus-visible:ring-1 focus-visible:ring-ring disabled:cursor-not-allowed disabled:opacity-50"
                >
                  {(activeModelList ?? []).map((m) => (
                    <option key={m} value={m}>{m}</option>
                  ))}
                </select>
                {isOllamaDapr && ollamaModelsLoading && (
                  <span className="block text-xs text-muted-foreground">
                    Loading models from Ollama server...
                  </span>
                )}
              </label>
            )}
            <div className="space-y-3 border-t border-border pt-4">
              <div>
                <h3 className="text-sm font-semibold">Execution environment</h3>
                <p className="text-xs text-muted-foreground">
                  Defaults inherited by member agents.
                </p>
              </div>
              {imageHistory.length > 0 && (
                <datalist id="image-history-suggestions">
                  {imageHistory.map((ref) => (
                    <option key={ref} value={ref} />
                  ))}
                </datalist>
              )}
              <div className="grid grid-cols-1 gap-4 sm:grid-cols-2">
                <label className="block space-y-1">
                  <span className="text-sm text-muted-foreground">Image (default)</span>
                  <input
                    list={imageHistory.length > 0 ? "image-history-suggestions" : undefined}
                    value={form.image}
                    onChange={(e) => {
                      if (imageSource === "base") setImageSource("applied");
                      update("image", e.target.value);
                    }}
                    placeholder="ghcr.io/cvoya-com/spring-voyage-agents:latest"
                    aria-label="Execution image"
                    className="flex h-9 w-full rounded-md border border-input bg-background px-3 py-1 text-sm shadow-sm transition-colors focus-visible:outline-none focus-visible:ring-1 focus-visible:ring-ring disabled:cursor-not-allowed disabled:opacity-50"
                  />
                </label>
                <label className="block space-y-1">
                  <span className="text-sm text-muted-foreground">Hosting mode</span>
                  <select
                    value={form.hosting}
                    onChange={(e) => update("hosting", e.target.value as HostingMode)}
                    aria-label="Hosting mode"
                    className="flex h-9 w-full rounded-md border border-input bg-background px-3 py-1 text-sm shadow-sm focus-visible:outline-none focus-visible:ring-1 focus-visible:ring-ring disabled:cursor-not-allowed disabled:opacity-50"
                  >
                    {HOSTING_MODES.map((m) => (
                      <option key={m.id} value={m.id}>{m.label}</option>
                    ))}
                  </select>
                </label>
              </div>
            </div>
            {stepError && (
              <p className="rounded-md border border-destructive/50 bg-destructive/10 px-3 py-2 text-sm text-destructive">
                {stepError}
              </p>
            )}
          </CardContent>
        </Card>
      )}

      {/* Step 4: Install for catalog, Connector for scratch */}
      {step === 4 && form.source === "catalog" && (
        <Card>
          <CardHeader>
            <CardTitle className="flex items-center gap-2">
              <Package className="h-5 w-5" /> Install
            </CardTitle>
          </CardHeader>
          <CardContent className="space-y-4 text-sm" data-testid="install-status-panel">
            <div className="rounded-md border border-border p-3 space-y-1">
              <SummaryRow label="Package" value={form.catalogPackageName ?? "—"} />
              <SummaryRow
                label="Connector"
                value={
                  form.connectorSlug === null
                    ? "(skipped)"
                    : form.connectorConfig === null
                      ? `${form.connectorSlug} (incomplete)`
                      : form.connectorSlug
                }
              />
            </div>
            {submitError && (
              <p className="rounded-md border border-destructive/50 bg-destructive/10 px-3 py-2 text-sm text-destructive">
                {submitError}
              </p>
            )}
            {installId && installPending && (
              <div
                role="status"
                data-testid="install-status-progress"
                className="rounded-md border border-border bg-muted/30 px-3 py-2 text-sm text-muted-foreground"
              >
                Installing {form.catalogPackageName}…
              </div>
            )}
            {installFailed && (
              <div
                role="alert"
                data-testid="install-status-failed"
                className="space-y-3 rounded-md border border-destructive/50 bg-destructive/5 px-3 py-2"
              >
                <p className="text-sm text-foreground">
                  Install failed.{" "}
                  {latestInstallStatus?.error && (
                    <span className="text-destructive">
                      {latestInstallStatus.error}
                    </span>
                  )}
                </p>
                <div className="flex flex-wrap gap-2">
                  <Button
                    variant="outline"
                    size="sm"
                    data-testid="install-retry-button"
                    onClick={() => retryInstallMutation.mutate()}
                    disabled={retryInstallMutation.isPending}
                  >
                    <RefreshCw className="mr-1.5 h-3.5 w-3.5" aria-hidden />
                    {retryInstallMutation.isPending ? "Retrying…" : "Retry"}
                  </Button>
                  <Button
                    variant="outline"
                    size="sm"
                    data-testid="install-abort-button"
                    onClick={() => abortInstallMutation.mutate()}
                    disabled={abortInstallMutation.isPending}
                  >
                    <X className="mr-1.5 h-3.5 w-3.5" aria-hidden />
                    {abortInstallMutation.isPending ? "Aborting…" : "Abort"}
                  </Button>
                </div>
              </div>
            )}
            {!installId && (
              <Button
                onClick={() => installMutation.mutate()}
                disabled={submitting}
                data-testid="install-unit-button"
              >
                {submitting ? "Installing…" : "Install"}
              </Button>
            )}
          </CardContent>
        </Card>
      )}

      {step === 4 && form.source === "scratch" && (
        <Card>
          <CardHeader>
            <CardTitle className="flex items-center gap-2">
              <Plug className="h-5 w-5" /> Connector
            </CardTitle>
          </CardHeader>
          <CardContent className="space-y-4 text-sm">
            <p className="text-muted-foreground">
              Optionally bind this unit to a connector during creation. Leave
              on <strong>Skip</strong> to configure it later.
            </p>
            {connectorTypesError && (
              <p className="rounded-md border border-destructive/50 bg-destructive/10 px-3 py-2 text-sm text-destructive">
                Failed to load connectors: {connectorTypesError}
              </p>
            )}
            <div className="space-y-2">
              <label className="flex cursor-pointer items-start gap-3 rounded-md border border-border p-3">
                <input
                  type="radio"
                  name="connector-choice"
                  checked={form.connectorSlug === null}
                  onChange={() =>
                    setForm((prev) => ({
                      ...prev,
                      connectorSlug: null,
                      connectorTypeId: null,
                      connectorConfig: null,
                    }))
                  }
                  className="mt-1"
                />
                <span>
                  <span className="font-medium">Skip</span>
                  <span className="block text-xs text-muted-foreground">
                    Create the unit without a connector binding.
                  </span>
                </span>
              </label>
              {connectorTypes?.map((c) => {
                const isSelected = form.connectorSlug === c.typeSlug;
                const WizardStep = getConnectorWizardStep(c.typeSlug);
                return (
                  <label
                    key={c.typeId}
                    className={cn(
                      "block space-y-2 rounded-md border p-3 transition-colors",
                      isSelected ? "border-primary bg-primary/5" : "border-border",
                    )}
                  >
                    <span className="flex cursor-pointer items-start gap-3">
                      <input
                        type="radio"
                        name="connector-choice"
                        checked={isSelected}
                        onChange={() =>
                          setForm((prev) => ({
                            ...prev,
                            connectorSlug: c.typeSlug,
                            connectorTypeId: c.typeId,
                            connectorConfig: null,
                          }))
                        }
                        className="mt-1"
                      />
                      <span className="flex-1">
                        <span className="font-medium">{c.displayName}</span>
                        <span className="block text-xs text-muted-foreground">
                          {c.description}
                        </span>
                      </span>
                    </span>
                    {isSelected && WizardStep && (
                      <WizardStep
                        onChange={handleConnectorConfigChange}
                        initialValue={form.connectorConfig}
                      />
                    )}
                    {isSelected && !WizardStep && (
                      <div className="rounded-md border border-amber-500/50 bg-amber-500/10 px-3 py-2 text-xs text-amber-900 dark:text-amber-200">
                        This connector doesn&apos;t ship a wizard UI. Select{" "}
                        <strong>Skip</strong> and configure it from the
                        unit&apos;s Connector tab after creation.
                      </div>
                    )}
                  </label>
                );
              })}
              {connectorTypes && connectorTypes.length === 0 && (
                <p className="rounded-md border border-border bg-muted/40 px-3 py-2 text-xs text-muted-foreground">
                  No connectors are registered on this server.
                </p>
              )}
            </div>
            {stepError && (
              <p className="rounded-md border border-destructive/50 bg-destructive/10 px-3 py-2 text-sm text-destructive">
                {stepError}
              </p>
            )}
          </CardContent>
        </Card>
      )}

      {/* Step 5 scratch: Install */}
      {step === 5 && form.source === "scratch" && (
        <Card>
          <CardHeader>
            <CardTitle className="flex items-center gap-2">
              <Terminal className="h-5 w-5" /> Install
            </CardTitle>
          </CardHeader>
          <CardContent className="space-y-4 text-sm" data-testid="install-status-panel">
            <div className="rounded-md border border-border p-3 space-y-1">
              <SummaryRow label="Name" value={renderNameSummary(form)} />
              <SummaryRow label="Display name" value={form.displayName || "—"} />
              <SummaryRow label="Description" value={form.description || "—"} />
              <SummaryRow label="Model" value={form.modelId || "(not selected)"} />
              <SummaryRow label="Image" value={form.image || "(leave to default)"} />
              <SummaryRow
                label="Connector"
                value={
                  form.connectorSlug === null
                    ? "(skipped)"
                    : form.connectorConfig === null
                      ? `${form.connectorSlug} (incomplete)`
                      : form.connectorSlug
                }
              />
            </div>
            {submitWarnings.length > 0 && (
              <SubmitWarningsPanel warnings={submitWarnings} />
            )}
            {submitError && (
              <p className="rounded-md border border-destructive/50 bg-destructive/10 px-3 py-2 text-sm text-destructive">
                {submitError}
              </p>
            )}
            {installId && installPending && (
              <div
                role="status"
                data-testid="install-status-progress"
                className="rounded-md border border-border bg-muted/30 px-3 py-2 text-sm text-muted-foreground"
              >
                Installing {form.name}…
              </div>
            )}
            {installFailed && (
              <div
                role="alert"
                data-testid="install-status-failed"
                className="space-y-3 rounded-md border border-destructive/50 bg-destructive/5 px-3 py-2"
              >
                <p className="text-sm text-foreground">
                  Install failed.{" "}
                  {latestInstallStatus?.error && (
                    <span className="text-destructive">
                      {latestInstallStatus.error}
                    </span>
                  )}
                </p>
                <div className="flex flex-wrap gap-2">
                  <Button
                    variant="outline"
                    size="sm"
                    data-testid="install-retry-button"
                    onClick={() => retryInstallMutation.mutate()}
                    disabled={retryInstallMutation.isPending}
                  >
                    <RefreshCw className="mr-1.5 h-3.5 w-3.5" aria-hidden />
                    {retryInstallMutation.isPending ? "Retrying…" : "Retry"}
                  </Button>
                  <Button
                    variant="outline"
                    size="sm"
                    data-testid="install-abort-button"
                    onClick={() => abortInstallMutation.mutate()}
                    disabled={abortInstallMutation.isPending}
                  >
                    <X className="mr-1.5 h-3.5 w-3.5" aria-hidden />
                    {abortInstallMutation.isPending ? "Aborting…" : "Abort"}
                  </Button>
                </div>
              </div>
            )}
            {!installId && (
              <Button
                onClick={() => installMutation.mutate()}
                disabled={submitting}
                data-testid="install-unit-button"
              >
                {submitting ? "Installing…" : "Install"}
              </Button>
            )}
          </CardContent>
        </Card>
      )}


      <div className="flex flex-col gap-2">
        <div className="flex items-center justify-between gap-3">
          <div className="flex items-center gap-2">
            <Button
              variant="outline"
              onClick={handleBack}
              disabled={
                step === 1 ||
                submitting ||
                installId !== null
              }
            >
              Back
            </Button>
            {installId === null && (
              <Button
                variant="ghost"
                onClick={handleCancel}
                disabled={submitting}
                data-testid="wizard-cancel"
              >
                Cancel
              </Button>
            )}
          </div>
          {step < maxStepForSource(form.source) && (
            <div className="flex flex-1 items-center justify-end gap-3">
              {nextDisabledReason && (
                <p
                  role="status"
                  aria-live="polite"
                  data-testid="next-disabled-reason"
                  className="max-w-md text-right text-xs text-muted-foreground"
                >
                  {nextDisabledReason}
                </p>
              )}
              <Button onClick={handleNext} disabled={!canGoNext}>
                Next
              </Button>
            </div>
          )}
        </div>
      </div>
    </div>
  );
}

// Exported for unit tests — `renderNameSummary` is pure, depending
// only on the form's `name` / `source` / `catalogPackageName` slots,
// so a direct helper test is cheaper than driving the wizard through
// every screen just to reach the Install summary.
export function renderNameSummary(
  form: Pick<FormState, "name" | "source" | "catalogPackageName">,
): string {
  const typedName = form.name.trim();
  if (typedName) return typedName;
  if (form.source === "catalog" && form.catalogPackageName) {
    return `(from package ${form.catalogPackageName})`;
  }
  return "—";
}

// ---------------------------------------------------------------------------
// #1509: Submit-warnings categorisation and panel
//
// The server returns two stable, well-understood warning shapes that are
// not operator errors. We pattern-match these and present them as info
// notices rather than alarming amber warnings. Any string that doesn't
// match a known pattern falls through to the "unknown" bucket and keeps
// the amber colour.
// ---------------------------------------------------------------------------

/** A "section not yet applied" warning from the manifest parser. */
const SECTION_NOT_APPLIED_RE =
  /^section '([^']+)' is parsed but not yet applied$/i;

/**
 * A tool-not-surfaced warning.
 * Groups: 1 = bundle path, 2 = tool name.
 */
const TOOL_NOT_SURFACED_RE =
  /^bundle '([^']+)' requires tool '([^']+)', which is not surfaced by any registered connector/i;

export type WarningCategory =
  | { kind: "section-not-applied"; section: string; raw: string }
  | { kind: "tool-not-surfaced"; bundle: string; tool: string; raw: string }
  | { kind: "unknown"; raw: string };

/** Classify a single server warning string into a typed category. */
export function categorizeWarning(warning: string): WarningCategory {
  const sectionMatch = SECTION_NOT_APPLIED_RE.exec(warning);
  if (sectionMatch) {
    return {
      kind: "section-not-applied",
      section: sectionMatch[1] ?? warning,
      raw: warning,
    };
  }
  const toolMatch = TOOL_NOT_SURFACED_RE.exec(warning);
  if (toolMatch) {
    return {
      kind: "tool-not-surfaced",
      bundle: toolMatch[1] ?? warning,
      tool: toolMatch[2] ?? warning,
      raw: warning,
    };
  }
  return { kind: "unknown", raw: warning };
}

/**
 * Return a connector hint derived from the bundle path, or null.
 * e.g. "spring-voyage/software-engineering/..." → "GitHub"
 */
function connectorHintFromBundle(bundle: string): string | null {
  const lower = bundle.toLowerCase();
  if (lower.includes("software-engineering") || lower.includes("github")) {
    return "GitHub";
  }
  if (lower.includes("jira") || lower.includes("issue-tracker")) {
    return "Jira";
  }
  if (lower.includes("slack")) {
    return "Slack";
  }
  return null;
}

/**
 * #1509: Collapsible warnings panel that groups server notices by category.
 *
 * - All-informational (no unknown warnings): info/blue-tinted box,
 *   title "Created with N notices", default-collapsed.
 * - Any unknown warning: amber box, title "Created with N warnings",
 *   default-expanded.
 * - Raw server text is always accessible in a nested disclosure element.
 */
function SubmitWarningsPanel({ warnings }: { warnings: string[] }) {
  const categorized = useMemo(
    () => warnings.map(categorizeWarning),
    [warnings],
  );

  const hasUnknown = categorized.some((c) => c.kind === "unknown");
  const allInformational = !hasUnknown;

  // Default-collapsed when all informational; default-expanded when any unknown.
  const [expanded, setExpanded] = useState<boolean>(!allInformational);
  const [rawVisible, setRawVisible] = useState(false);

  const sectionNotApplied = categorized.filter(
    (c): c is Extract<WarningCategory, { kind: "section-not-applied" }> =>
      c.kind === "section-not-applied",
  );
  const toolNotSurfaced = categorized.filter(
    (c): c is Extract<WarningCategory, { kind: "tool-not-surfaced" }> =>
      c.kind === "tool-not-surfaced",
  );
  const unknown = categorized.filter(
    (c): c is Extract<WarningCategory, { kind: "unknown" }> =>
      c.kind === "unknown",
  );

  const count = warnings.length;

  if (allInformational) {
    return (
      <div
        role="status"
        data-testid="submit-warnings-panel"
        className="rounded-md border border-primary/40 bg-primary/10 px-3 py-2 text-sm"
      >
        <button
          type="button"
          aria-expanded={expanded}
          onClick={() => setExpanded((v) => !v)}
          className="flex w-full items-center gap-2 text-left focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring focus-visible:ring-offset-1"
        >
          <Info className="h-4 w-4 shrink-0 text-primary" aria-hidden />
          <span className="flex-1 font-medium text-foreground">
            Created with {count} {count === 1 ? "notice" : "notices"}
          </span>
          {expanded ? (
            <ChevronDown
              className="h-4 w-4 shrink-0 text-muted-foreground"
              aria-hidden
            />
          ) : (
            <ChevronRight
              className="h-4 w-4 shrink-0 text-muted-foreground"
              aria-hidden
            />
          )}
        </button>

        {expanded && (
          <div className="mt-2 space-y-3 text-foreground">
            {sectionNotApplied.length > 0 && (
              <div>
                <p className="text-xs font-medium text-muted-foreground">
                  Some manifest sections are accepted but not yet applied
                </p>
                <p className="mt-0.5 text-xs text-muted-foreground">
                  These sections are parsed and stored but will take effect in a
                  future release.
                </p>
                <ul className="mt-1 list-disc pl-5 text-xs">
                  {sectionNotApplied.map((c) => (
                    <li key={c.section}>{c.section}</li>
                  ))}
                </ul>
              </div>
            )}

            {toolNotSurfaced.length > 0 && (
              <div>
                <p className="text-xs font-medium text-muted-foreground">
                  Tools that need a connector binding
                </p>
                <p className="mt-0.5 text-xs text-muted-foreground">
                  Bind the relevant connector from the unit&apos;s Connector tab
                  after creation.
                </p>
                <ul className="mt-1 list-disc pl-5 text-xs">
                  {toolNotSurfaced.map((c) => {
                    const hint = connectorHintFromBundle(c.bundle);
                    return (
                      <li key={`${c.bundle}/${c.tool}`}>
                        <span className="font-mono">{c.tool}</span>
                        {hint
                          ? ` — bind a ${hint} connector`
                          : " — bind a connector"}
                      </li>
                    );
                  })}
                </ul>
              </div>
            )}

            <div className="text-xs">
              <button
                type="button"
                onClick={() => setRawVisible((v) => !v)}
                className="select-none text-muted-foreground hover:text-foreground"
              >
                {rawVisible ? "Hide" : "Show"} raw server messages
              </button>
              {rawVisible && (
                <ul className="mt-1 list-disc pl-5 text-muted-foreground">
                  {warnings.map((w, i) => (
                    <li key={i}>{w}</li>
                  ))}
                </ul>
              )}
            </div>
          </div>
        )}
      </div>
    );
  }

  // Mixed or fully-unknown warnings: amber, default-expanded.
  return (
    <div
      role="alert"
      data-testid="submit-warnings-panel"
      className="rounded-md border border-amber-500/50 bg-amber-500/10 px-3 py-2 text-sm"
    >
      <button
        type="button"
        aria-expanded={expanded}
        onClick={() => setExpanded((v) => !v)}
        className="flex w-full items-center gap-2 text-left focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring focus-visible:ring-offset-1"
      >
        <AlertTriangle
          className="h-4 w-4 shrink-0 text-amber-600 dark:text-amber-400"
          aria-hidden
        />
        <span className="flex-1 font-medium text-amber-900 dark:text-amber-200">
          Created with {count} {count === 1 ? "warning" : "warnings"}
        </span>
        {expanded ? (
          <ChevronDown
            className="h-4 w-4 shrink-0 text-amber-600/70 dark:text-amber-400/70"
            aria-hidden
          />
        ) : (
          <ChevronRight
            className="h-4 w-4 shrink-0 text-amber-600/70 dark:text-amber-400/70"
            aria-hidden
          />
        )}
      </button>

      {expanded && (
        <div className="mt-2 space-y-3 text-amber-900 dark:text-amber-200">
          {sectionNotApplied.length > 0 && (
            <div>
              <p className="text-xs font-medium">
                Some manifest sections are accepted but not yet applied
              </p>
              <p className="mt-0.5 text-xs opacity-80">
                These sections are parsed and stored but will take effect in a
                future release.
              </p>
              <ul className="mt-1 list-disc pl-5 text-xs">
                {sectionNotApplied.map((c) => (
                  <li key={c.section}>{c.section}</li>
                ))}
              </ul>
            </div>
          )}

          {toolNotSurfaced.length > 0 && (
            <div>
              <p className="text-xs font-medium">
                Tools that need a connector binding
              </p>
              <p className="mt-0.5 text-xs opacity-80">
                Bind the relevant connector from the unit&apos;s Connector tab
                after creation.
              </p>
              <ul className="mt-1 list-disc pl-5 text-xs">
                {toolNotSurfaced.map((c) => {
                  const hint = connectorHintFromBundle(c.bundle);
                  return (
                    <li key={`${c.bundle}/${c.tool}`}>
                      <span className="font-mono">{c.tool}</span>
                      {hint
                        ? ` — bind a ${hint} connector`
                        : " — bind a connector"}
                    </li>
                  );
                })}
              </ul>
            </div>
          )}

          {unknown.length > 0 && (
            <div>
              <p className="text-xs font-medium">Other notices</p>
              <ul className="mt-1 list-disc pl-5 text-xs">
                {unknown.map((c, i) => (
                  <li key={i}>{c.raw}</li>
                ))}
              </ul>
            </div>
          )}

          <div className="text-xs">
            <button
              type="button"
              onClick={() => setRawVisible((v) => !v)}
              className="select-none opacity-70 hover:opacity-100"
            >
              {rawVisible ? "Hide" : "Show"} raw server messages
            </button>
            {rawVisible && (
              <ul className="mt-1 list-disc pl-5 opacity-80">
                {warnings.map((w, i) => (
                  <li key={i}>{w}</li>
                ))}
              </ul>
            )}
          </div>
        </div>
      )}
    </div>
  );
}

/**
 * #1615: renders the catalog package's declared inputs as a typed form.
 * Each entry produces one field whose control matches the input type
 * (`string` → text, `int` → number, `bool` → checkbox); secret-flagged
 * inputs render as password fields. Required inputs without a value AND
 * no default surface an inline hint and bubble up via
 * `missingRequiredCatalogInputs` so the wizard's Next button stays
 * disabled until the operator fills them. The placeholder "no inputs"
 * panel from the v0.1 stub is gone — when the package declares no
 * inputs the panel collapses to a one-line note.
 */
function CatalogInputsPanel({
  packageName,
  inputs,
  values,
  onChange,
  loading,
  error,
  missingRequired,
}: {
  packageName: string;
  inputs: PackageInputSummary[];
  values: Record<string, string>;
  onChange: (name: string, value: string) => void;
  loading: boolean;
  error: string | null;
  missingRequired: string[];
}) {
  if (loading) {
    return (
      <div
        className="rounded-md border border-border bg-muted/20 px-3 py-2 text-xs text-muted-foreground"
        data-testid="catalog-inputs-loading"
      >
        <p className="font-medium text-foreground">Package inputs</p>
        <p className="mt-0.5">Loading inputs schema for {packageName}…</p>
      </div>
    );
  }

  if (error) {
    return (
      <div
        className="rounded-md border border-destructive/50 bg-destructive/10 px-3 py-2 text-xs text-destructive"
        role="alert"
        data-testid="catalog-inputs-error"
      >
        Could not load inputs schema: {error}
      </div>
    );
  }

  if (inputs.length === 0) {
    return (
      <div
        className="rounded-md border border-border bg-muted/20 px-3 py-2 text-xs text-muted-foreground"
        data-testid="catalog-inputs-empty"
      >
        <p className="font-medium text-foreground">Package inputs</p>
        <p className="mt-0.5">
          <code className="font-mono">{packageName}</code> declares no inputs.
        </p>
      </div>
    );
  }

  const missingSet = new Set(missingRequired);

  return (
    <div
      className="space-y-3 rounded-md border border-border bg-muted/10 px-3 py-3"
      data-testid="catalog-inputs"
    >
      <p className="text-sm font-medium text-foreground">Package inputs</p>
      {inputs.map((def) => {
        const name = def.name ?? "";
        if (!name) return null;
        const type = (def.type ?? "string").toLowerCase();
        const isSecret = def.secret === true;
        const isBool = type === "bool" || type === "boolean";
        const isInt = type === "int" || type === "integer";
        const isRequired = def.required === true;
        const value = values[name] ?? "";
        const showMissing = missingSet.has(name);
        const placeholderText =
          typeof def.default === "string" && def.default !== ""
            ? `default: ${def.default}`
            : undefined;

        return (
          <label
            key={name}
            className="block space-y-1"
            data-testid={`catalog-input-${name}`}
          >
            <span className="flex items-center gap-1 text-sm">
              <code className="font-mono text-xs">{name}</code>
              {isRequired && (
                <span
                  className="text-destructive"
                  aria-label="required"
                  title="Required"
                >
                  *
                </span>
              )}
              <span className="text-[11px] text-muted-foreground">
                ({isSecret ? "secret" : type})
              </span>
            </span>
            {isBool ? (
              <label className="flex items-center gap-2 text-sm">
                <input
                  type="checkbox"
                  checked={value === "true"}
                  onChange={(e) =>
                    onChange(name, e.target.checked ? "true" : "false")
                  }
                  data-testid={`catalog-input-${name}-control`}
                  className="h-4 w-4 rounded border-input"
                />
                <span className="text-xs text-muted-foreground">
                  {value === "true" ? "true" : "false"}
                </span>
              </label>
            ) : (
              <Input
                type={isSecret ? "password" : isInt ? "number" : "text"}
                value={value}
                onChange={(e) => onChange(name, e.target.value)}
                placeholder={placeholderText}
                data-testid={`catalog-input-${name}-control`}
                aria-required={isRequired}
                aria-invalid={showMissing}
              />
            )}
            {def.description && (
              <span className="block text-xs text-muted-foreground">
                {def.description}
              </span>
            )}
            {showMissing && (
              <span
                className="block text-xs text-destructive"
                data-testid={`catalog-input-${name}-missing`}
              >
                This input is required.
              </span>
            )}
          </label>
        );
      })}
    </div>
  );
}

function SourceCard({
  icon,
  title,
  description,
  selected,
  onSelect,
  testId,
}: {
  icon: React.ReactNode;
  title: string;
  description: string;
  selected: boolean;
  onSelect: () => void;
  testId?: string;
}) {
  return (
    <button
      type="button"
      onClick={onSelect}
      aria-pressed={selected}
      data-testid={testId}
      className={cn(
        "flex w-full items-start gap-3 rounded-md border p-4 text-left transition-colors focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring focus-visible:ring-offset-2",
        selected
          ? "border-primary bg-primary/5 shadow-sm"
          : "border-border hover:border-primary/40 hover:bg-accent/50",
      )}
    >
      <div
        className={cn(
          "mt-0.5 flex h-10 w-10 shrink-0 items-center justify-center rounded-md border border-border bg-muted text-muted-foreground transition-colors",
          selected && "border-primary/40 bg-primary/15 text-primary",
        )}
      >
        {icon}
      </div>
      <div className="flex-1">
        <div className="flex items-center gap-2">
          <span className="text-sm font-medium">{title}</span>
        </div>
        <div className="text-xs text-muted-foreground">{description}</div>
      </div>
    </button>
  );
}

function SummaryRow({ label, value }: { label: string; value: string }) {
  return (
    <div className="flex justify-between gap-4">
      <span className="text-muted-foreground">{label}</span>
      <span className="font-mono text-xs text-right">{value}</span>
    </div>
  );
}

/**
 * #626: inline credential flow. Replaces the PR #627 "status badge +
 * deep link" card with a richer surface that lets the operator supply
 * an LLM API key from inside the wizard — either as a unit-scoped
 * secret (toggle off) or as a new tenant default (toggle on).
 *
 * Rendered states:
 *   - `requiredProvider === null` → nothing (Ollama/custom paths).
 *   - probe pending → nothing (the Provider dropdown already paints a
 *     loading state; a flashing "checking…" line would add noise).
 *   - Ollama (spring-voyage + provider=ollama) → reuses PR #627's
 *     reachability banner verbatim. No inline input — Ollama doesn't
 *     use API keys.
 *   - probe error → muted "could not verify" line.
 *   - resolvable (unit or tenant) → green confirmation badge. When the
 *     source is `tenant` we show an "Override" button that opens the
 *     same inline input as the "not configured" path; the operator
 *     can then type a new value to save per-unit or overwrite the
 *     tenant default.
 *   - not resolvable → amber banner + inline credential input +
 *     "Save as tenant default" checkbox.
 *
 * Key material never round-trips: the typed value lives in React
 * state just long enough to be POSTed. The probe endpoint is also
 * key-free by design (PR #627), so we can re-render confidently from
 * its response without ever seeing plaintext.
 */
type KeyedProviderId = "anthropic" | "openai" | "google";

interface CredentialSectionProps {
  /**
   * ADR-0038: provider id of the installed model-provider entry the
   * wizard needs a credential for. Restricted to the closed set of
   * key-bearing providers — Ollama (no credential) flows through the
   * dedicated reachability banner outside this component.
   */
  requiredProvider: KeyedProviderId | null;
  status:
    | import("@/lib/api/types").ProviderCredentialStatusResponse
    | null;
  statusPending: boolean;
  statusError: boolean;
  credentialKey: string;
  saveAsTenantDefault: boolean;
  overrideOpen: boolean;
  ollamaProbe:
    | import("@/lib/api/types").ProviderCredentialStatusResponse
    | null;
  onKeyChange: (value: string) => void;
  onToggleSaveAsTenantDefault: (value: boolean) => void;
  onToggleOverride: (value: boolean) => void;
}

function CredentialSection(props: CredentialSectionProps) {
  const {
    requiredProvider,
    status,
    statusPending,
    statusError,
    credentialKey,
    saveAsTenantDefault,
    overrideOpen,
    ollamaProbe,
    onKeyChange,
    onToggleSaveAsTenantDefault,
    onToggleOverride,
  } = props;

  // Ollama reachability banner is still useful even when no API key is
  // required. Render it standalone (same shape as PR #627) when the
  // probe was run against Ollama.
  if (requiredProvider === null) {
    if (!ollamaProbe) return null;
    return <OllamaReachabilityBanner data={ollamaProbe} />;
  }

  const displayName = providerLabel(requiredProvider);

  if (statusPending) return null;

  if (statusError || !status) {
    // Even when the probe fails, the user still needs to enter an API
    // key to proceed. Render the input alongside a muted "could not
    // verify" message so the wizard never dead-ends on a flaky probe.
    // `status?.suggestion` carries an optional operator-facing hint from
    // the backend (may be absent on older servers — optional chaining
    // keeps this path working regardless of backend shape).
    const probeHint = status?.suggestion ?? null;
    return (
      <div
        role="status"
        data-testid="credential-status"
        data-resolvable="unknown"
        className="space-y-3 rounded-md border border-warning/50 bg-warning/15 px-3 py-3 text-sm text-foreground"
      >
        <div className="flex items-start gap-2">
          <AlertTriangle className="mt-0.5 h-4 w-4 shrink-0" aria-hidden />
          <p className="flex-1">
            {probeHint ?? `Could not verify ${displayName} credentials.`} Enter
            a key below to save it as a unit-scoped secret, or tick the box to
            save it as your tenant default.
          </p>
        </div>
        <CredentialInputControls
          provider={requiredProvider}
          credentialKey={credentialKey}
          saveAsTenantDefault={saveAsTenantDefault}
          onKeyChange={onKeyChange}
          onToggleSaveAsTenantDefault={onToggleSaveAsTenantDefault}
          tenantToggleLabel={`Use this key as the default for all future units using ${displayName}.`}
        />
      </div>
    );
  }

  if (status.resolvable) {
    const sourceText =
      status.source === "unit"
        ? `${displayName} credentials: set on unit`
        : status.source === "tenant"
          ? `${displayName} credentials: inherited from tenant default`
          : // Defensive — shouldn't happen for Anthropic/OpenAI/Google.
            `${displayName} credentials resolvable`;
    return (
      <div className="space-y-2">
        <div
          role="status"
          data-testid="credential-status"
          data-resolvable="true"
          data-source={status.source ?? ""}
          className="flex items-start gap-2 rounded-md border border-emerald-500/40 bg-emerald-500/10 px-3 py-2 text-sm text-emerald-900 dark:text-emerald-200"
        >
          <CheckCircle2 className="mt-0.5 h-4 w-4 shrink-0" aria-hidden />
          <span className="flex-1">{sourceText}</span>
          {status.source === "tenant" && !overrideOpen && (
            <button
              type="button"
              data-testid="credential-override-link"
              onClick={() => onToggleOverride(true)}
              className="text-xs font-medium underline underline-offset-2 text-emerald-900 dark:text-emerald-100"
            >
              Override
            </button>
          )}
        </div>
        {overrideOpen && (
          <>
            <p className="text-xs text-muted-foreground">
              The existing tenant default stays in place until you save a
              new value. The current value is not shown — type a
              replacement below, or click Cancel to keep the existing
              default.
            </p>
            <CredentialInputControls
              provider={requiredProvider}
              credentialKey={credentialKey}
              saveAsTenantDefault={saveAsTenantDefault}
              onKeyChange={onKeyChange}
              onToggleSaveAsTenantDefault={onToggleSaveAsTenantDefault}
              tenantToggleLabel={`Overwrite the tenant default for all future units using ${displayName}.`}
            />
            <button
              type="button"
              data-testid="credential-override-cancel"
              onClick={() => onToggleOverride(false)}
              className="text-xs font-medium underline underline-offset-2 text-muted-foreground"
            >
              Cancel override
            </button>
          </>
        )}
      </div>
    );
  }

  // Not configured — show the inline input with the save-as-tenant-
  // default checkbox. The banner itself uses PR #599/#610's axe-clean
  // warning palette (warning/50 border + warning/15 fill).
  return (
    <div
      role="alert"
      data-testid="credential-status"
      data-resolvable="false"
      className="space-y-3 rounded-md border border-warning/50 bg-warning/15 px-3 py-3 text-sm text-foreground"
    >
      <div className="flex items-start gap-2">
        <AlertTriangle className="mt-0.5 h-4 w-4 shrink-0" aria-hidden />
        <p className="flex-1">
          {displayName} credentials: not configured. Enter a key below to
          save it as a unit-scoped secret, or tick the box to save it as
          your tenant default.
        </p>
      </div>
      <CredentialInputControls
        provider={requiredProvider}
        credentialKey={credentialKey}
        saveAsTenantDefault={saveAsTenantDefault}
        onKeyChange={onKeyChange}
        onToggleSaveAsTenantDefault={onToggleSaveAsTenantDefault}
        tenantToggleLabel={`Use this key as the default for all future units using ${displayName}.`}
      />
    </div>
  );
}

/**
 * The shared input + "show / hide" toggle + "Save as tenant default"
 * checkbox used by both the "not configured" and "override" flows.
 * Extracted so the two call sites cannot drift apart on labelling or
 * accessibility attributes.
 *
 * Issue #659: we render a small "Get an API key" deep link next to
 * the input so operators without a key can create one without leaving
 * the wizard. For Anthropic specifically, the hint clarifies that the
 * field expects a Console API key — not a Claude Code CLI OAuth token
 * from `claude setup-token`. OAuth-token support is tracked as a
 * separate follow-up; this PR only surfaces the distinction in copy.
 */
function CredentialInputControls({
  provider,
  credentialKey,
  saveAsTenantDefault,
  onKeyChange,
  onToggleSaveAsTenantDefault,
  tenantToggleLabel,
}: {
  provider: "anthropic" | "openai" | "google";
  credentialKey: string;
  saveAsTenantDefault: boolean;
  onKeyChange: (value: string) => void;
  onToggleSaveAsTenantDefault: (value: boolean) => void;
  tenantToggleLabel: string;
}) {
  const [show, setShow] = useState(false);
  const inputId = `credential-key-${provider}`;
  const toggleId = `credential-save-tenant-${provider}`;
  const inputType = show ? "text" : "password";
  const displayName = providerLabel(provider);
  const helpLink = PROVIDER_KEY_HELP[provider];
  const anthropicClarification =
    provider === "anthropic"
      ? "Accepts a Console API key (sk-ant-api…) or a Claude.ai token from claude setup-token (sk-ant-oat…). Claude.ai tokens require the claude CLI on the host."
      : null;

  // #660: Anthropic accepts two credential formats — a Platform API key
  // (from console.anthropic.com, starts with `sk-ant-api...`) and a
  // Claude.ai OAuth token (from `claude setup-token`, starts with
  // `sk-ant-oat...`). The label/placeholder reflect both so operators
  // on a Claude.ai subscription (no Platform plan) know the token
  // they already have will work.
  const isAnthropic = provider === "anthropic";
  const fieldLabel = isAnthropic
    ? `${displayName} API key or Claude.ai token`
    : `${displayName} API key`;
  const fieldPlaceholder = isAnthropic
    ? "Paste your Anthropic API key or Claude.ai token"
    : `Paste your ${displayName} API key`;
  const showHideLabel = isAnthropic ? "credential" : "API key";

  return (
    <div className="space-y-2">
      <label htmlFor={inputId} className="block space-y-1">
        <div className="flex flex-wrap items-center justify-between gap-2">
          <span className="text-xs text-muted-foreground">{fieldLabel}</span>
          <a
            href={helpLink.href}
            target="_blank"
            rel="noopener noreferrer"
            data-testid="credential-help-link"
            className="inline-flex items-center gap-1 text-xs font-medium text-primary underline-offset-2 hover:underline"
          >
            Get an API key
            <ExternalLink className="h-3 w-3" aria-hidden />
          </a>
        </div>
        <div className="flex items-center gap-2">
          <Input
            id={inputId}
            type={inputType}
            value={credentialKey}
            onChange={(e) => onKeyChange(e.target.value)}
            placeholder={fieldPlaceholder}
            autoComplete="off"
            spellCheck={false}
            data-testid="credential-input"
          />
          <Button
            type="button"
            size="sm"
            variant="outline"
            onClick={() => setShow((s) => !s)}
            aria-label={
              show
                ? `Hide ${displayName} ${showHideLabel}`
                : `Show ${displayName} ${showHideLabel}`
            }
            aria-pressed={show}
            data-testid="credential-visibility-toggle"
          >
            {show ? (
              <>
                <EyeOff className="mr-1 h-3.5 w-3.5" aria-hidden /> Hide
              </>
            ) : (
              <>
                <Eye className="mr-1 h-3.5 w-3.5" aria-hidden /> Show
              </>
            )}
          </Button>
        </div>
      </label>

      {anthropicClarification && (
        <p
          className="text-[11px] text-muted-foreground"
          data-testid="credential-help-anthropic"
        >
          {anthropicClarification}
        </p>
      )}

      {/*
        T-07 (#949): the wizard no longer validates the key against the
        LLM. The backend runs validation after the unit is created and
        the detail-page Validation panel reports the outcome. So this
        section only collects + persists the key — there is no inline
        verdict here anymore.
      */}

      <label
        htmlFor={toggleId}
        className="flex items-start gap-2 text-xs text-muted-foreground"
      >
        <input
          id={toggleId}
          type="checkbox"
          checked={saveAsTenantDefault}
          onChange={(e) => onToggleSaveAsTenantDefault(e.target.checked)}
          className="mt-0.5"
          data-testid="credential-save-as-tenant-default"
        />
        <span>{tenantToggleLabel}</span>
      </label>
    </div>
  );
}

/**
 * Standalone Ollama reachability banner. PR #627 defined the shape;
 * #626 just factors it out so the new `CredentialSection` can reuse
 * it verbatim when the tool is `spring-voyage + provider=ollama`.
 */
function OllamaReachabilityBanner({
  data,
}: {
  data: import("@/lib/api/types").ProviderCredentialStatusResponse;
}) {
  if (data.resolvable) {
    return (
      <div
        role="status"
        data-testid="credential-status"
        data-resolvable="true"
        data-source=""
        className="flex items-start gap-2 rounded-md border border-emerald-500/40 bg-emerald-500/10 px-3 py-2 text-sm text-emerald-900 dark:text-emerald-200"
      >
        <CheckCircle2 className="mt-0.5 h-4 w-4 shrink-0" aria-hidden />
        <span>Ollama reachable</span>
      </div>
    );
  }
  return (
    <div
      role="alert"
      data-testid="credential-status"
      data-resolvable="false"
      className="flex items-start gap-2 rounded-md border border-warning/50 bg-warning/15 px-3 py-2 text-sm text-foreground"
    >
      <AlertTriangle className="mt-0.5 h-4 w-4 shrink-0" aria-hidden />
      <p>
        {data.suggestion ??
          "Ollama not reachable. Check that the Ollama server is running."}
      </p>
    </div>
  );
}

/**
 * Step 5 row that surfaces the tenant-default LLM credential for the
 * selected runtime (read-only) alongside an "Override" affordance. This
 * closes the gap where Step 5 previously showed "No secrets queued" even
 * when a tenant default existed — operators had no way to tell whether
 * the unit would inherit a key or needed one queued. The override flow
 * mirrors Step 2's: opening it reveals the shared credential input; the
 * entered value is written as a unit-scoped secret (or a new tenant
 * default when the checkbox is ticked) during the Finalize submit.
 */
function TenantDefaultSecretRow({
  provider,
  secretName,
  overrideOpen,
  credentialKey,
  saveAsTenantDefault,
  onToggleOverride,
  onKeyChange,
  onToggleSaveAsTenantDefault,
}: {
  provider: "anthropic" | "openai" | "google";
  secretName: string;
  overrideOpen: boolean;
  credentialKey: string;
  saveAsTenantDefault: boolean;
  onToggleOverride: (value: boolean) => void;
  onKeyChange: (value: string) => void;
  onToggleSaveAsTenantDefault: (value: boolean) => void;
}) {
  const displayName = providerLabel(provider);
  return (
    <div
      data-testid="tenant-default-secret-row"
      data-provider={provider}
      className="space-y-2 rounded-md border border-border bg-muted/30 p-3"
    >
      <div className="flex items-start justify-between gap-3">
        <div className="flex-1">
          <div className="flex items-center gap-2">
            <CheckCircle2
              className="h-4 w-4 shrink-0 text-emerald-600 dark:text-emerald-400"
              aria-hidden
            />
            <span className="font-medium">
              {displayName} tenant default
            </span>
          </div>
          {secretName && (
            <p className="mt-1 text-xs text-muted-foreground">
              This unit will inherit the tenant-default secret{" "}
              <code className="rounded bg-background px-1 py-0.5 font-mono text-[11px]">
                {secretName}
              </code>{" "}
              at dispatch time. No action needed unless you want to override
              it for this unit.
            </p>
          )}
        </div>
        {!overrideOpen && (
          <Button
            size="sm"
            variant="outline"
            onClick={() => onToggleOverride(true)}
            data-testid="tenant-default-override"
          >
            Override
          </Button>
        )}
      </div>
      {overrideOpen && (
        <div className="space-y-2 rounded-md border border-border bg-background p-3">
          <p className="text-xs text-muted-foreground">
            The existing tenant default stays in place until you save a new
            value. The current value is not shown — type a replacement below,
            or click Cancel to keep the tenant default.
          </p>
          <CredentialInputControls
            provider={provider}
            credentialKey={credentialKey}
            saveAsTenantDefault={saveAsTenantDefault}
            onKeyChange={onKeyChange}
            onToggleSaveAsTenantDefault={onToggleSaveAsTenantDefault}
            tenantToggleLabel={`Overwrite the tenant default for all future units using ${displayName}.`}
          />
          <button
            type="button"
            data-testid="tenant-default-override-cancel"
            onClick={() => onToggleOverride(false)}
            className="text-xs font-medium underline underline-offset-2 text-muted-foreground"
          >
            Cancel override
          </button>
        </div>
      )}
    </div>
  );
}

function providerLabel(providerId: string): string {
  switch (providerId) {
    case "claude":
    case "anthropic":
      return "Anthropic";
    case "openai":
      return "OpenAI";
    case "google":
    case "gemini":
    case "googleai":
      return "Google";
    case "ollama":
      return "Ollama";
    default:
      return providerId;
  }
}
