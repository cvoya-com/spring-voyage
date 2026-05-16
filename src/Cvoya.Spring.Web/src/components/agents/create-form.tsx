"use client";

import { useCallback, useEffect, useMemo, useState } from "react";
import { useQuery, useQueryClient } from "@tanstack/react-query";
import Link from "next/link";
import {
  AlertTriangle,
  CheckCircle2,
  Package,
  Plug,
  Search,
  Sparkles,
} from "lucide-react";

import { Badge } from "@/components/ui/badge";
import { ApiErrorMessage } from "@/components/ui/api-error-message";
import { Button } from "@/components/ui/button";
import {
  Card,
  CardContent,
  CardHeader,
  CardTitle,
} from "@/components/ui/card";
import { Input } from "@/components/ui/input";
import { Skeleton } from "@/components/ui/skeleton";
import { useToast } from "@/components/ui/toast";
import { api, ApiError } from "@/lib/api/client";
import { formatTranslatedError } from "@/lib/api/translate-error";
import {
  useModelProviderModels,
  useModelProviders,
  useProviderCredentialStatus,
  useConnectorBindings,
  usePackage,
  useUnitExecution,
} from "@/lib/api/queries";
import { queryKeys } from "@/lib/api/query-keys";
import {
  AGENT_NAME_PATTERN,
  buildCreateAgentRequest,
} from "@/lib/agents/create-agent";
import {
  canonicalUnitId,
  parseMultiParentInheritanceConflict,
  type MultiParentInheritanceConflict,
} from "@/lib/agents/multi-parent-conflict";
import { cn } from "@/lib/utils";
import {
  DEFAULT_RUNTIME_ID,
  HOSTING_MODES,
  RUNTIMES,
  RUNTIME_LIST,
  getAllowedProviders,
  getFixedProvider,
  type HostingMode,
  type RuntimeId,
} from "@/lib/ai-models";
import {
  providerDisplayName,
  runtimeCredentialDescriptor,
  type RuntimeCredentialDescriptor,
} from "@/lib/runtime-credentials";
import type {
  PackageConnectorBindings,
  PackageDetail,
  UnitResponse,
} from "@/lib/api/types";
import { SourcePackagePicker } from "./source-package-picker";

// ---------------------------------------------------------------------------
// Types
// ---------------------------------------------------------------------------

/**
 * Internal form state owned by `<AgentCreateForm>`. Mirrors the wire-level
 * create-agent request shape so the runtime / model / image fields land
 * in `definitionJson` alongside `displayName`, `description`, `role`, and
 * `unitIds`.
 *
 * Per ADR-0039 I4 / DESIGN.md §12.6, every execution-block field is
 * **inheritable**: an empty string means "inherit from parent unit (or
 * tenant defaults)" — the backend resolves the actual value at dispatch
 * time. The form surfaces the inherited value via an italic placeholder
 * + help-copy with `data-testid="inherit-indicator"`.
 */
interface FormState {
  id: string;
  displayName: string;
  role: string;
  description: string;
  /**
   * ADR-0038 agent runtime id (`ai.runtime`). Empty string means inherit
   * from the selected unit (or tenant default — `claude-code`).
   */
  runtime: RuntimeId | "";
  /**
   * ADR-0038 model provider id (`ai.model.provider`). Empty string means
   * inherit. Fixed-provider runtimes snap this to the runtime's fixed
   * provider when the operator picks an explicit runtime.
   */
  modelProviderId: string;
  /** ADR-0038 model id within the provider's catalogue (`ai.model.id`). */
  modelId: string;
  /** Container image (`execution.image`). Empty string means inherit. */
  image: string;
  /** Hosting mode (`execution.hosting`). Empty string means inherit. */
  hosting: HostingMode | "";
  unitIds: string[];
}

type AgentSource = "scratch" | "from-package" | "browse";
type AgentCreateContext = "page" | "dialog";
type PageBranch = "source" | "scratch" | "from-package" | "browse";

type SubmitPhase =
  | "idle"
  | "creating"
  | "done"
  | "failed";
type SuccessVariant = "agent" | "unit" | "installed";

interface CreateState {
  agentId: string | null;
  installId: string | null;
  phase: SubmitPhase;
  error: unknown | null;
  successVariant: SuccessVariant | null;
  /**
   * ADR-0039 §6 (I6): structured 422 from the create call. When
   * non-null, the form renders an inline conflict block listing every
   * diverging field and the parent-attributed values, and blocks the
   * submit button until the operator either trims the parent set or
   * sets the field explicitly. Cleared on any form-state change.
   */
  multiParentConflict: MultiParentInheritanceConflict | null;
}

const INITIAL_CREATE: CreateState = {
  agentId: null,
  installId: null,
  phase: "idle",
  error: null,
  successVariant: null,
  multiParentConflict: null,
};

// ---------------------------------------------------------------------------
// Props
// ---------------------------------------------------------------------------

/**
 * Successful-create payload surfaced to the caller. `agentId` is the
 * server-allocated id when the API returns one; `unitIds` is the
 * (possibly empty) selected parent set in the form's navigation-friendly
 * unit-name shape.
 */
export interface AgentCreateSuccess {
  agentId?: string;
  installId?: string;
  successVariant?: SuccessVariant;
  unitIds: string[];
}

export interface AgentCreateFormSnapshot {
  source: AgentSource;
  name: string;
  displayName: string;
  description: string;
  role: string;
  runtime: string;
  modelProviderId: string;
  modelId: string;
  hosting: string;
  image: string;
}

export type AgentCreateFormInitialSnapshot =
  Partial<AgentCreateFormSnapshot>;

export interface AgentCreateFormProps {
  /**
   * Page mode starts with the ADR-0039 K1 Source step. Dialog mode skips
   * Source and starts from `initialSource` when provided, otherwise scratch.
   */
  context: AgentCreateContext;
  /**
   * Optional source override for shells that skip the page-level Source
   * step but still need to start in a non-scratch branch.
   */
  initialSource?: AgentSource;
  /**
   * Optional initial unit ids (URL-safe names). Useful for the
   * unit-tab dialog (J1) where the dialog opens "from" a specific unit
   * and the assignment should default to it. Empty by default — the
   * standalone page does not pre-select anything.
   */
  initialUnitIds?: string[];
  /**
   * Optional initial values for the standalone page wizard persistence
   * path. Unit-tab dialogs intentionally omit this so dialog state stays
   * ephemeral.
   */
  initialSnapshot?: AgentCreateFormInitialSnapshot;
  /**
   * Emits the page wizard's secrets-free form snapshot on state changes.
   * The standalone page persists it; dialog callers leave it unwired.
   */
  onSnapshotChange?: (snapshot: AgentCreateFormSnapshot) => void;
  /**
   * Called after the direct create endpoint returns 201. The standalone
   * page navigates to `/units?node=<first>&tab=Agents` when a parent unit
   * was selected, or `/units` for a top-level tenant-parented agent. A
   * dialog caller might close itself instead. Receives the successful-create
   * summary.
   */
  onSuccess?: (result: AgentCreateSuccess) => void;
  /**
   * Called when the operator clicks Cancel / Back. The standalone page
   * uses this to call `router.back()`; a dialog caller closes itself.
   * When omitted, the cancel/back buttons stay visible but no-op.
   */
  onCancel?: () => void;
  /**
   * Called when a non-page branch backs out to the scratch form. Page mode
   * handles this internally by returning to the Source step.
   */
  onSourceBack?: () => void;
}

// ---------------------------------------------------------------------------
// Component
// ---------------------------------------------------------------------------

function normalizeSource(source: string | undefined): AgentSource | null {
  if (
    source === "scratch" ||
    source === "from-package" ||
    source === "browse"
  ) {
    return source;
  }
  return null;
}

function normalizeRuntime(runtime: string | undefined): RuntimeId | "" {
  if (!runtime) return "";
  return runtime in RUNTIMES ? (runtime as RuntimeId) : "";
}

function normalizeHosting(hosting: string | undefined): HostingMode | "" {
  if (!hosting) return "";
  return HOSTING_MODES.some((mode) => mode.id === hosting)
    ? (hosting as HostingMode)
    : "";
}

/**
 * Reusable agent-create form. Owns identity, execution, and unit-assignment
 * fields plus the direct scratch-create flow (`POST /api/v1/tenant/agents`).
 * Extracted from `app/agents/create/page.tsx` (ADR-0039 I3) so the unit-tab
 * dialog (J1) can embed the same form without forking the wire-format
 * helpers.
 *
 * Visual chrome reuses the existing `<Card>` / `<Input>` / `<Button>`
 * primitives — DESIGN.md does not need an update for this extraction.
 */
export function AgentCreateForm({
  context,
  initialSource: initialSourceProp,
  initialUnitIds = [],
  initialSnapshot,
  onSnapshotChange,
  onSuccess,
  onCancel,
  onSourceBack,
}: AgentCreateFormProps) {
  const queryClient = useQueryClient();
  const { toast } = useToast();

  const initialForm = useMemo<FormState>(
    () => ({
      id: initialSnapshot?.name ?? "",
      displayName: initialSnapshot?.displayName ?? "",
      role: initialSnapshot?.role ?? "",
      description: initialSnapshot?.description ?? "",
      // ADR-0039 I4 / DESIGN.md §12.6: default every execution field to
      // inherit-mode (empty). The placeholder + help-copy below the
      // field shows the resolved parent value so the operator sees what
      // they will inherit; an explicit pick overrides it.
      runtime: normalizeRuntime(initialSnapshot?.runtime),
      modelProviderId: initialSnapshot?.modelProviderId ?? "",
      modelId: initialSnapshot?.modelId ?? "",
      image: initialSnapshot?.image ?? "",
      hosting: normalizeHosting(initialSnapshot?.hosting),
      unitIds: [...initialUnitIds],
    }),
    [initialSnapshot, initialUnitIds],
  );

  const initialSource = useMemo<AgentSource>(
    () =>
      normalizeSource(initialSourceProp ?? initialSnapshot?.source) ??
      "scratch",
    [initialSourceProp, initialSnapshot?.source],
  );
  const [form, setForm] = useState<FormState>(initialForm);
  const [source, setSource] = useState<AgentSource>(initialSource);
  const [sourcePackageName, setSourcePackageName] = useState<string | null>(
    null,
  );
  const [packageInputs, setPackageInputs] = useState<Record<string, string>>(
    {},
  );
  const [
    connectorBindingSelections,
    setConnectorBindingSelections,
  ] = useState<Record<string, string>>({});
  const [pageBranch, setPageBranch] = useState<PageBranch>(() => {
    if (context === "page") return "source";
    if (initialSource === "from-package") return "from-package";
    if (initialSource === "browse") return "browse";
    return "scratch";
  });
  const [validationMessage, setValidationMessage] = useState<string | null>(null);
  const [create, setCreate] = useState<CreateState>(INITIAL_CREATE);
  // #2246: "Activate automatically after creation" preference shown next to
  // the submit button. For persistent agents this triggers an eager deploy;
  // for ephemeral agents the deploy call is skipped (they activate on demand).
  // Per-session UI preference; not persisted into the wizard snapshot.
  const [autoDeploy, setAutoDeploy] = useState(true);

  const clearSubmitFeedback = () => {
    setValidationMessage(null);
    setCreate((prev) => {
      if (
        prev.phase === "failed" ||
        prev.error !== null ||
        prev.multiParentConflict !== null
      ) {
        return INITIAL_CREATE;
      }
      return prev;
    });
  };

  const selectSourcePackage = (packageName: string | null) => {
    if (packageName !== sourcePackageName) {
      setPackageInputs({});
      setConnectorBindingSelections({});
    }
    setSourcePackageName(packageName);
  };

  useEffect(() => {
    if (!onSnapshotChange || context !== "page") return;
    onSnapshotChange({
      source,
      name: form.id,
      displayName: form.displayName,
      description: form.description,
      role: form.role,
      runtime: form.runtime,
      modelProviderId: form.modelProviderId,
      modelId: form.modelId,
      hosting: form.hosting,
      image: form.image,
    });
  }, [context, form, onSnapshotChange, source]);

  // ── Form helpers ───────────────────────────────────────────────────────

  const update = <K extends keyof FormState>(key: K, value: FormState[K]) => {
    setForm((prev) => ({ ...prev, [key]: value }));
    clearSubmitFeedback();
  };

  // ── Queries ────────────────────────────────────────────────────────────

  const unitsQuery = useQuery<UnitResponse[]>({
    queryKey: queryKeys.units.list(),
    queryFn: () => api.listUnits(),
    staleTime: 30_000,
  });

  const providersQuery = useModelProviders();
  const providers = useMemo(
    () => providersQuery.data ?? [],
    [providersQuery.data],
  );

  // ── Inherit-from-parent context (ADR-0039 I4) ──────────────────────────
  //
  // The "inherited value" we surface in the per-field placeholder + help
  // copy depends on the selected unit set:
  //   * 0 units → tenant defaults; we don't have a tenant-defaults endpoint
  //     yet so fall back to platform defaults (claude-code, etc.) and
  //     name the parent "tenant defaults".
  //   * 1 unit → that unit's own resolved execution block.
  //   * >1 units → the selected parents may diverge; show the generic
  //     "parent" source without a concrete value and let backend
  //     validation return the structured conflict if needed.
  //
  // We load `useUnitExecution` only for the single-parent case so the
  // values surface as soon as the operator ticks exactly one unit.
  const hasMultipleSelectedUnits = form.unitIds.length > 1;
  const selectedUnitKey =
    form.unitIds.length === 1 ? form.unitIds[0] : null;
  const selectedUnit = useMemo(
    () =>
      (unitsQuery.data ?? []).find(
        (u) => u.name === selectedUnitKey || u.id === selectedUnitKey,
      ) ?? null,
    [unitsQuery.data, selectedUnitKey],
  );
  const selectedUnitName = selectedUnit?.name ?? selectedUnitKey;
  const selectedUnitExecutionQuery = useUnitExecution(
    selectedUnitName ?? "",
    { enabled: Boolean(selectedUnitName) },
  );

  /** Display name for the inherit-source — unit name, or "tenant defaults". */
  const inheritSourceLabel: string = hasMultipleSelectedUnits
    ? "parent"
    : selectedUnit?.displayName?.trim() ||
      selectedUnit?.name ||
      "tenant defaults";

  const inheritedRuntimeForPicker =
    normalizeRuntime(selectedUnitExecutionQuery.data?.runtime ?? undefined) ||
    DEFAULT_RUNTIME_ID;

  const selectedSourcePackageQuery = usePackage(sourcePackageName ?? "", {
    enabled: Boolean(sourcePackageName),
  });
  const selectedPackageManifest = selectedSourcePackageQuery.data ?? null;

  // ADR-0039 I4: when `form.runtime === ""` the operator wants to inherit;
  // gating logic for the provider/model dropdowns still needs to resolve
  // against *some* runtime. Use the inherited (or platform default)
  // runtime as the effective key for those decisions.
  const effectiveRuntime: RuntimeId =
    (form.runtime || inheritedRuntimeForPicker) as RuntimeId;

  // ADR-0038 §1: the runtime descriptor decides whether the provider
  // is fixed or operator-picked.
  const runtimeDescriptor = RUNTIMES[effectiveRuntime];
  const fixedProviderId = getFixedProvider(effectiveRuntime);
  const pickerProviders = useMemo(() => {
    if (runtimeDescriptor.isProviderFixed) return [];
    const allowed = getAllowedProviders(effectiveRuntime) ?? [];
    return providers.filter((p) =>
      (allowed as readonly string[]).includes(p.id),
    );
  }, [providers, effectiveRuntime, runtimeDescriptor.isProviderFixed]);

  // ADR-0038: when the operator has *explicitly* picked a runtime that
  // fixes its provider, snap `modelProviderId` to the runtime's fixed
  // provider so the wire shape matches. When the runtime is in inherit
  // mode (`form.runtime === ""`) we leave modelProviderId untouched so
  // its own inherit affordance renders.
  if (
    form.runtime !== "" &&
    fixedProviderId !== null &&
    form.modelProviderId !== fixedProviderId
  ) {
    setForm((prev) =>
      prev.modelProviderId === fixedProviderId
        ? prev
        : { ...prev, modelProviderId: fixedProviderId },
    );
  }

  // ADR-0038: runtime-aware banner — replaces the generic "no providers
  // installed" fallback with a message that names the provider(s) the
  // *currently selected* runtime actually needs. Skip when the catalog
  // is still loading, when the runtime is the deferred `custom` slot,
  // or when the operator left runtime in inherit mode (no explicit
  // pick — the unit's resolved value is what matters at dispatch).
  const runtimeProviderIssue = useMemo<string | null>(() => {
    if (form.runtime === "" || form.runtime === "custom") return null;
    if (providersQuery.isPending) return null;
    if (providersQuery.isError) {
      return "Could not load the model-provider catalogue.";
    }
    const installedIds = new Set(providers.map((p) => p.id.toLowerCase()));
    if (runtimeDescriptor.isProviderFixed) {
      const fixed = runtimeDescriptor.fixedProvider;
      if (fixed !== null && !installedIds.has(fixed.toLowerCase())) {
        return `${runtimeDescriptor.displayName} requires the ${fixed} model provider, which is not installed on this tenant. Install it via the host (\`spring model-provider install ${fixed}\`) or pick a different runtime.`;
      }
      return null;
    }
    const allowed = runtimeDescriptor.allowedProviders;
    if (allowed.length === 0) return null;
    const intersection = allowed.filter((id) =>
      installedIds.has(id.toLowerCase()),
    );
    if (intersection.length === 0) {
      return `${runtimeDescriptor.displayName} needs at least one model provider installed. Install via the host (e.g. \`spring model-provider install anthropic\`) or pick a fixed-provider runtime.`;
    }
    return null;
  }, [
    form.runtime,
    runtimeDescriptor,
    providers,
    providersQuery.isPending,
    providersQuery.isError,
  ]);

  // Effective provider for the model catalogue lookup. When the runtime
  // is in inherit mode we still want the model dropdown to populate so
  // the operator can override the model alone; pick the inherited (or
  // first installed) provider as the lookup target.
  const effectiveProviderId = useMemo<string>(() => {
    if (form.modelProviderId !== "") return form.modelProviderId;
    if (fixedProviderId !== null) return fixedProviderId;
    return pickerProviders.length > 0 ? pickerProviders[0].id : "";
  }, [form.modelProviderId, fixedProviderId, pickerProviders]);

  const activeProviderId = effectiveProviderId.trim().toLowerCase();
  const credentialDescriptor = useMemo(
    () =>
      runtimeCredentialDescriptor(
        effectiveRuntime,
        activeProviderId.length > 0 ? activeProviderId : null,
      ),
    [effectiveRuntime, activeProviderId],
  );
  const modelsQuery = useModelProviderModels(activeProviderId, {
    enabled: Boolean(activeProviderId),
  });

  /**
   * Resolve the inherited value for one execution slot. Returns `null`
   * when there is nothing to surface (the platform-side default is not
   * known on the client) so the caller can fall back to a generic copy.
   */
  const inheritedValue = useCallback(
    (slot: "runtime" | "modelProvider" | "modelId" | "image" | "hosting"): string | null => {
      const unitDefaults = selectedUnitExecutionQuery.data ?? null;
      if (hasMultipleSelectedUnits) return null;
      // 1-unit case: use the unit's own /execution row.
      if (selectedUnitName) {
        switch (slot) {
          case "runtime":
            return unitDefaults?.runtime ?? DEFAULT_RUNTIME_ID;
          case "modelProvider":
            return (
              unitDefaults?.model?.provider ??
              getFixedProvider(DEFAULT_RUNTIME_ID)
            );
          case "modelId":
            return unitDefaults?.model?.id ?? null;
          case "image":
            return unitDefaults?.image ?? null;
          case "hosting":
            // UnitExecutionResponse does not carry a hosting slot —
            // hosting is agent-exclusive. Inherit from the platform
            // default at dispatch time.
            return null;
          default:
            return null;
        }
      }
      // 0-unit case: tenant defaults. No tenant-defaults endpoint yet
      // (#TBD); fall back to the platform defaults the dispatcher uses.
      switch (slot) {
        case "runtime":
          return DEFAULT_RUNTIME_ID;
        case "modelProvider":
          return getFixedProvider(DEFAULT_RUNTIME_ID);
        case "modelId":
          return null;
        case "image":
          return null;
        case "hosting":
          return null;
        default:
          return null;
      }
    },
    [hasMultipleSelectedUnits, selectedUnitName, selectedUnitExecutionQuery.data],
  );

  const modelOptions = useMemo(() => {
    if (activeProviderId) {
      const list = modelsQuery.data ?? [];
      return list.map((m) => ({ id: m.id, label: m.displayName ?? m.id }));
    }
    return providers.flatMap((p) =>
      (p.models ?? []).map((m) => ({
        id: m,
        label: `${m} — ${p.displayName}`,
      })),
    );
  }, [activeProviderId, modelsQuery.data, providers]);

  // ── Create flow ────────────────────────────────────────────────────────

  const invalidateAgentCaches = useCallback(() => {
    queryClient.invalidateQueries({ queryKey: queryKeys.agents.all });
    queryClient.invalidateQueries({ queryKey: queryKeys.units.all });
    queryClient.invalidateQueries({ queryKey: queryKeys.dashboard.all });
    // #2387: tenant.tree() is invalidated by the activity SSE handler
    // (`queryKeysAffectedBySource` for `agent://…`) — no manual call needed.
  }, [queryClient]);

  const resolveSelectedUnits = useCallback(() => {
    const units = unitsQuery.data ?? [];
    const selectedKeys = form.unitIds
      .map((u) => u.trim())
      .filter((u) => u.length > 0);
    const apiUnitIds: string[] = [];
    const navigationUnitIds: string[] = [];
    const seenApiIds = new Set<string>();
    const seenNavigationIds = new Set<string>();

    for (const key of selectedKeys) {
      const unit =
        units.find((u) => u.id === key || u.name === key) ?? null;
      const apiId = unit?.id ?? key;
      const navigationId = unit?.name ?? key;

      if (!seenApiIds.has(apiId)) {
        apiUnitIds.push(apiId);
        seenApiIds.add(apiId);
      }
      if (!seenNavigationIds.has(navigationId)) {
        navigationUnitIds.push(navigationId);
        seenNavigationIds.add(navigationId);
      }
    }

    return { apiUnitIds, navigationUnitIds };
  }, [form.unitIds, unitsQuery.data]);

  const buildPackageConnectorBindings = useCallback(():
    | PackageConnectorBindings
    | undefined => {
    const requirements = connectorRequirements(selectedPackageManifest);
    if (requirements.length === 0) return undefined;

    const packageBindings: NonNullable<PackageConnectorBindings["package"]> =
      {};
    for (const connectorType of requirements) {
      const bindingId = connectorBindingSelections[connectorType]?.trim();
      if (!bindingId) continue;
      packageBindings[connectorType] = {
        config: { bindingId },
      };
    }

    return Object.keys(packageBindings).length > 0
      ? { package: packageBindings, units: null }
      : undefined;
  }, [connectorBindingSelections, selectedPackageManifest]);

  const runCreate = useCallback(async () => {
    const localAgentId = form.id.trim();
    const { apiUnitIds, navigationUnitIds } = resolveSelectedUnits();

    setCreate({
      agentId: localAgentId,
      installId: null,
      phase: "creating",
      error: null,
      successVariant: null,
      multiParentConflict: null,
    });

    try {
      if (source === "from-package" && sourcePackageName) {
        const connectorBindings = buildPackageConnectorBindings();
        const response = await api.installPackages([
          {
            packageName: sourcePackageName,
            inputs: packageInputs,
            ...(connectorBindings ? { connectorBindings } : {}),
          },
        ]);
        // #2246: auto-deploy persistent agents that the package install
        // declared (the server reports them as createdAgentIds in the
        // response). Failures are non-fatal — the install row exists; the
        // operator can deploy manually from the agent detail view.
        if (autoDeploy) {
          const agentIds = response.packages.flatMap(
            (p) => p.createdAgentIds ?? [],
          );
          if (agentIds.length > 0) {
            await Promise.all(
              agentIds.map((id) =>
                api.deployPersistentAgent(id).catch(() => null),
              ),
            );
          }
        }
        const packageManifest =
          selectedPackageManifest ??
          (await api.getPackage(sourcePackageName).catch(() => null));
        const successVariant =
          successVariantForPackage(packageManifest);

        setCreate((prev) => ({
          ...prev,
          installId: response.installId,
          phase: "done",
          successVariant,
        }));
        invalidateAgentCaches();

        toast({
          title: successMessageForVariant(successVariant),
          description: response.installId,
        });
        onSuccess?.({
          installId: response.installId,
          successVariant,
          unitIds: navigationUnitIds,
        });
        return;
      }

      // ADR-0039 I4: blank fields submit as undefined so the backend
      // resolves the parent unit's value (or tenant default) at dispatch.
      // When the operator picked a fixed-provider runtime explicitly,
      // snap modelProvider to the runtime's fixed value.
      const submittedProvider =
        form.runtime !== "" && fixedProviderId !== null
          ? fixedProviderId
          : form.modelProviderId;
      const body = buildCreateAgentRequest({
        displayName: form.displayName.trim(),
        description: form.description,
        role: form.role,
        image: form.image,
        hosting: form.hosting || null,
        runtime: form.runtime || undefined,
        model: {
          provider: submittedProvider,
          id: form.modelId,
        },
        unitIds: apiUnitIds,
      });

      const response = await api.createAgent(body);
      const createdAgentId =
        response.id !== undefined && response.id !== null
          ? String(response.id)
          : localAgentId;

      // #2246: when the operator left "Deploy automatically" checked and
      // picked persistent hosting, deploy the just-created agent so it
      // starts running without a separate trip to the agent detail page.
      // Failures are non-fatal — the agent row exists; the operator can
      // deploy manually from the detail view.
      if (autoDeploy && form.hosting === "persistent") {
        try {
          await api.deployPersistentAgent(createdAgentId);
        } catch {
          // Best-effort.
        }
      }

      setCreate((prev) => ({
        ...prev,
        agentId: createdAgentId,
        phase: "done",
        successVariant: "agent",
      }));
      invalidateAgentCaches();

      toast({ title: "Agent created", description: createdAgentId });
      onSuccess?.({ agentId: createdAgentId, unitIds: navigationUnitIds });
    } catch (err) {
      if (err instanceof ApiError && err.status === 422) {
        const conflict = parseMultiParentInheritanceConflict(err.body);
        if (conflict !== null) {
          setCreate((prev) => ({
            ...prev,
            phase: "failed",
            error: null,
            multiParentConflict: conflict,
          }));
          return;
        }
      }

      const msg = formatTranslatedError(err);
      setCreate((prev) => ({ ...prev, phase: "failed", error: err }));
      toast({
        title: "Create failed",
        description: msg,
        variant: "destructive",
      });
    }
  }, [
    form,
    source,
    sourcePackageName,
    packageInputs,
    selectedPackageManifest,
    buildPackageConnectorBindings,
    fixedProviderId,
    resolveSelectedUnits,
    invalidateAgentCaches,
    onSuccess,
    toast,
    autoDeploy,
  ]);

  const handleSubmit = (e: React.FormEvent<HTMLFormElement>) => {
    e.preventDefault();
    if (source === "from-package") {
      if (!sourcePackageName) {
        setValidationMessage("Select a package to install.");
        return;
      }
      const missingConnectorBindings = selectedPackageManifest
        ? connectorRequirements(selectedPackageManifest).filter(
            (connectorType) =>
              !connectorBindingSelections[connectorType]?.trim(),
          )
        : [];
      if (missingConnectorBindings.length > 0) {
        setValidationMessage(
          `Select connector bindings for ${missingConnectorBindings.join(", ")}.`,
        );
        return;
      }
      setValidationMessage(null);
      void runCreate();
      return;
    }

    const agentId = form.id.trim();
    if (!agentId) {
      setValidationMessage("Agent id is required.");
      return;
    }
    if (!AGENT_NAME_PATTERN.test(agentId)) {
      setValidationMessage(
        "Agent id must be URL-safe (lowercase letters, digits, and hyphens).",
      );
      return;
    }
    if (!form.displayName.trim()) {
      setValidationMessage("Display name is required.");
      return;
    }
    setValidationMessage(null);
    void runCreate();
  };

  const handleCancel = () => {
    onCancel?.();
  };

  const handleSourceNext = () => {
    if (source === "scratch") {
      setPageBranch("scratch");
      return;
    }

    if (source === "from-package") {
      setPageBranch("from-package");
      return;
    }

    if (source === "browse") {
      setPageBranch("browse");
    }
  };

  const handleSourceBack = () => {
    setSource("scratch");
    selectSourcePackage(null);
    if (context === "page") {
      setPageBranch("source");
      return;
    }
    setPageBranch("scratch");
    onSourceBack?.();
  };

  // ── Derived UI state ───────────────────────────────────────────────────

  const submitting = create.phase === "creating";

  // ADR-0039 I4 — flips the Execution card header badge between
  // `Inherits` (everything blank) and `Configured` (any explicit pick).
  const executionHasOverride =
    form.runtime !== "" ||
    form.modelProviderId !== "" ||
    form.modelId !== "" ||
    form.image !== "" ||
    form.hosting !== "";

  const idleSubmitLabel =
    source === "from-package" ? "Install package" : "Create agent";
  const progressLabel =
    source === "from-package" ? "Installing…" : "Creating…";
  const phaseLabel: Record<SubmitPhase, string> = {
    idle: idleSubmitLabel,
    creating: progressLabel,
    done: idleSubmitLabel,
    failed: idleSubmitLabel,
  };

  if (context === "page" && pageBranch === "source") {
    return (
      <div className="space-y-4">
        <Card>
          <CardHeader>
            <CardTitle>Choose a source</CardTitle>
          </CardHeader>
          <CardContent className="space-y-3">
            <SourceCard
              icon={<Sparkles className="h-5 w-5" aria-hidden />}
              title="Scratch"
              description="Start from scratch. Define the agent's identity, execution config, and units."
              selected={source === "scratch"}
              onSelect={() => {
                setSource("scratch");
                selectSourcePackage(null);
              }}
              testId="agent-source-card-scratch"
            />

            <SourceCard
              icon={<Package className="h-5 w-5" aria-hidden />}
              title="From package"
              description="Install from a package. Choose a package that contains an agent template."
              selected={source === "from-package"}
              onSelect={() => setSource("from-package")}
              testId="agent-source-card-from-package"
            />

            <SourceCard
              icon={<Search className="h-5 w-5" aria-hidden />}
              title="Browse"
              description="Browse the registry. (Coming soon)"
              selected={source === "browse"}
              onSelect={() => {
                setSource("browse");
                selectSourcePackage(null);
              }}
              testId="agent-source-card-browse"
            />
          </CardContent>
        </Card>

        <div className="flex items-center justify-end gap-2">
          <Button type="button" variant="outline" onClick={handleCancel}>
            Cancel
          </Button>
          <Button
            type="button"
            onClick={handleSourceNext}
          >
            Next
          </Button>
        </div>
      </div>
    );
  }

  if (pageBranch === "from-package") {
    return (
      <SourcePackagePicker
        selectedPackageName={sourcePackageName}
        onSelectionChange={selectSourcePackage}
        onSelect={(packageName) => {
          selectSourcePackage(packageName);
          setPageBranch("scratch");
        }}
        onBack={handleSourceBack}
        onCancel={handleCancel}
        selectionDetail={
          <PackageConnectorRequirementsPanel
            packageName={sourcePackageName}
            selectedBindings={connectorBindingSelections}
            onBindingChange={(connectorType, bindingId) =>
              setConnectorBindingSelections((prev) => ({
                ...prev,
                [connectorType]: bindingId,
              }))
            }
          />
        }
      />
    );
  }

  if (context === "page" && pageBranch === "browse") {
    return (
      <div className="space-y-4">
        <Card>
          <CardHeader>
            <CardTitle className="flex items-center gap-2">
              <Search className="h-5 w-5" aria-hidden />
              Browse agent packages
            </CardTitle>
          </CardHeader>
          <CardContent className="space-y-4">
            <div
              role="status"
              data-testid="browse-coming-soon"
              className="space-y-3 rounded-md border border-border bg-muted/30 px-4 py-6 text-center"
            >
              <Package
                className="mx-auto h-8 w-8 text-muted-foreground"
                aria-hidden
              />
              <p className="text-sm font-medium">Coming soon</p>
              <p className="text-xs text-muted-foreground">
                Search the Spring Voyage package registry for community
                packages. (Coming soon — use the CLI for now.)
              </p>
            </div>
          </CardContent>
        </Card>

        <div className="flex items-center justify-between gap-2">
          <Button type="button" variant="outline" onClick={handleSourceBack}>
            Back
          </Button>
          <div className="flex items-center gap-2">
            <Button type="button" variant="outline" onClick={handleCancel}>
              Cancel
            </Button>
            <Button type="button" disabled>
              Next
            </Button>
          </div>
        </div>
      </div>
    );
  }

  return (
    <form onSubmit={handleSubmit} noValidate>
      {sourcePackageName && (
        <div
          role="status"
          className="mb-4 flex items-start gap-2 rounded-md border border-primary/40 bg-primary/10 px-3 py-2 text-sm"
        >
          <Package
            className="mt-0.5 h-4 w-4 shrink-0 text-primary"
            aria-hidden
          />
          <div className="min-w-0">
            <p className="font-medium">Package selected</p>
            <p className="truncate font-mono text-xs text-muted-foreground">
              {sourcePackageName}
            </p>
          </div>
        </div>
      )}

      {/* ── Identity ──────────────────────────────────────────────── */}
      <Card>
        <CardHeader>
          <CardTitle>Identity</CardTitle>
        </CardHeader>
        <CardContent className="space-y-4">
          <label className="block space-y-1">
            <span className="text-sm text-muted-foreground">
              Agent id <span className="text-destructive">*</span>
            </span>
            <Input
              value={form.id}
              onChange={(e) => update("id", e.target.value)}
              placeholder="ada"
              pattern={AGENT_NAME_PATTERN.source}
              aria-label="Agent id"
              aria-required="true"
              autoComplete="off"
              spellCheck={false}
              disabled={submitting}
              required
            />
            <span className="block text-xs text-muted-foreground">
              URL-safe — lowercase letters, digits, and hyphens only.
            </span>
          </label>

          <label className="block space-y-1">
            <span className="text-sm text-muted-foreground">
              Display name <span className="text-destructive">*</span>
            </span>
            <Input
              value={form.displayName}
              onChange={(e) => update("displayName", e.target.value)}
              placeholder="Ada Lovelace"
              aria-label="Display name"
              aria-required="true"
              disabled={submitting}
              required
            />
          </label>

          <label className="block space-y-1">
            <span className="text-sm text-muted-foreground">Role (optional)</span>
            <Input
              value={form.role}
              onChange={(e) => update("role", e.target.value)}
              placeholder="reviewer"
              aria-label="Role"
              disabled={submitting}
            />
          </label>

          <label className="block space-y-1">
            <span className="text-sm text-muted-foreground">Description (optional)</span>
            <Input
              value={form.description}
              onChange={(e) => update("description", e.target.value)}
              placeholder="Short description of this agent's purpose"
              aria-label="Description"
              disabled={submitting}
            />
          </label>
        </CardContent>
      </Card>

      {/* ── Execution ─────────────────────────────────────────────── */}
      {/*
        ADR-0039 I4 / DESIGN.md §12.6 — every Execution-block field is
        independently inheritable. When the operator leaves a field blank
        we surface the inherited value as an italic placeholder + help
        copy below the field with `data-testid="inherit-indicator"`. An
        explicit pick toggles the field to "configured"; the per-field
        Use-inherited-value button reverts.
      */}
      <Card className="mt-4">
        <CardHeader className="flex flex-row items-center justify-between gap-2 space-y-0">
          <CardTitle>Execution</CardTitle>
          {executionHasOverride ? (
            <Badge
              variant="outline"
              className="text-xs font-normal"
              data-testid="execution-card-badge"
            >
              Configured
            </Badge>
          ) : (
            <Badge
              variant="outline"
              className="text-xs font-normal"
              data-testid="execution-card-badge"
            >
              Inherits
            </Badge>
          )}
        </CardHeader>
        <CardContent className="space-y-4">
          {runtimeProviderIssue && (
            <div
              role="alert"
              data-testid="model-provider-catalog-issue"
              className="flex items-start gap-2 rounded-md border border-warning/50 bg-warning/15 px-3 py-2 text-sm text-foreground"
            >
              <AlertTriangle className="mt-0.5 h-4 w-4 shrink-0" aria-hidden />
              <p className="flex-1">{runtimeProviderIssue}</p>
            </div>
          )}

          {/* Agent runtime — `runtime` field. */}
          <InheritableField
            label="Agent runtime"
            help={
              <>
                ADR-0038 launcher key. Mirrors{" "}
                <code className="font-mono">--runtime</code>.
              </>
            }
            isInherited={form.runtime === ""}
            inheritSourceLabel={inheritSourceLabel}
            inheritedValue={inheritedValue("runtime")}
            onClear={
              form.runtime !== ""
                ? () => {
                    setForm((prev) => ({
                      ...prev,
                      runtime: "",
                      modelId: "",
                    }));
                    clearSubmitFeedback();
                  }
                : undefined
            }
            disabled={submitting}
          >
            <select
              value={form.runtime}
              onChange={(e) => {
                const nextRuntime = e.target.value as RuntimeId | "";
                const nextFixed =
                  nextRuntime === "" ? null : getFixedProvider(nextRuntime);
                setForm((prev) => ({
                  ...prev,
                  runtime: nextRuntime,
                  // ADR-0038: when the operator picks a fixed-provider
                  // runtime, snap the provider id; otherwise leave the
                  // operator's prior choice intact.
                  modelProviderId: nextFixed ?? prev.modelProviderId,
                  modelId: "",
                }));
                clearSubmitFeedback();
              }}
              aria-label="Agent runtime"
              data-testid="agent-create-runtime-select"
              disabled={submitting}
              className={`flex h-9 w-full rounded-md border border-input bg-background px-3 text-sm shadow-sm focus-visible:outline-none focus-visible:ring-1 focus-visible:ring-ring disabled:cursor-not-allowed disabled:opacity-50${
                form.runtime === ""
                  ? " italic text-muted-foreground"
                  : ""
              }`}
            >
              <option value="">
                {inheritedValue("runtime") !== null
                  ? `inherited from ${inheritSourceLabel}: ${inheritedValue("runtime")}`
                  : `inherited from ${inheritSourceLabel}`}
              </option>
              {RUNTIME_LIST.map((r) => (
                <option key={r.id} value={r.id}>
                  {r.displayName}
                </option>
              ))}
            </select>
          </InheritableField>

          {/* Model provider — only rendered when the *currently selected*
              runtime is multi-provider. When the runtime itself is
              inherited, fall back to the platform default's posture so
              the picker only shows for genuinely multi-provider setups. */}
          {!runtimeDescriptor.isProviderFixed && (
            <InheritableField
              label="Model provider"
              help={
                <>
                  Mirrors{" "}
                  <code className="font-mono">--model-provider</code>.
                </>
              }
              isInherited={form.modelProviderId === ""}
              inheritSourceLabel={inheritSourceLabel}
              inheritedValue={inheritedValue("modelProvider")}
              onClear={
                form.modelProviderId !== ""
                  ? () => {
                      setForm((prev) => ({
                        ...prev,
                        modelProviderId: "",
                        modelId: "",
                      }));
                      clearSubmitFeedback();
                    }
                  : undefined
              }
              disabled={submitting}
            >
              <select
                value={form.modelProviderId}
                onChange={(e) => {
                  setForm((prev) => ({
                    ...prev,
                    modelProviderId: e.target.value,
                    modelId: "",
                  }));
                  clearSubmitFeedback();
                }}
                aria-label="Model provider"
                data-testid="agent-create-model-provider-select"
                disabled={submitting || pickerProviders.length === 0}
                className={`flex h-9 w-full rounded-md border border-input bg-background px-3 text-sm shadow-sm focus-visible:outline-none focus-visible:ring-1 focus-visible:ring-ring disabled:cursor-not-allowed disabled:opacity-50${
                  form.modelProviderId === ""
                    ? " italic text-muted-foreground"
                    : ""
                }`}
              >
                <option value="">
                  {pickerProviders.length === 0
                    ? "(no providers installed)"
                    : inheritedValue("modelProvider") !== null
                      ? `inherited from ${inheritSourceLabel}: ${inheritedValue("modelProvider")}`
                      : `inherited from ${inheritSourceLabel}`}
                </option>
                {pickerProviders.map((p) => (
                  <option key={p.id} value={p.id}>
                    {p.displayName}
                  </option>
                ))}
              </select>
            </InheritableField>
          )}

          {/* Container image — `image` field. */}
          <InheritableField
            label="Container image"
            help={
              <>
                Persisted under{" "}
                <code className="font-mono">execution.image</code>. Mirrors{" "}
                <code className="font-mono">--image</code>.
              </>
            }
            isInherited={form.image === ""}
            inheritSourceLabel={inheritSourceLabel}
            inheritedValue={inheritedValue("image")}
            onClear={
              form.image !== ""
                ? () => {
                    setForm((prev) => ({ ...prev, image: "" }));
                    clearSubmitFeedback();
                  }
                : undefined
            }
            disabled={submitting}
          >
            <Input
              value={form.image}
              onChange={(e) => update("image", e.target.value)}
              placeholder={
                inheritedValue("image") !== null
                  ? `inherited from ${inheritSourceLabel}: ${inheritedValue("image")}`
                  : `inherited from ${inheritSourceLabel}`
              }
              aria-label="Container image"
              disabled={submitting}
              className={
                form.image === ""
                  ? "italic text-muted-foreground placeholder:italic placeholder:text-muted-foreground"
                  : undefined
              }
            />
          </InheritableField>

          {/* Model — `model.id` field. */}
          <InheritableField
            label="Model"
            help={
              modelOptions.length === 0 && !providersQuery.isPending
                ? "No models available for this provider. The agent will inherit the parent's default model at dispatch."
                : "Model identifier within the selected provider's catalogue."
            }
            isInherited={form.modelId === ""}
            inheritSourceLabel={inheritSourceLabel}
            inheritedValue={inheritedValue("modelId")}
            onClear={
              form.modelId !== ""
                ? () => {
                    setForm((prev) => ({ ...prev, modelId: "" }));
                    clearSubmitFeedback();
                  }
                : undefined
            }
            disabled={submitting}
          >
            <select
              value={form.modelId}
              onChange={(e) => update("modelId", e.target.value)}
              aria-label="Model"
              data-testid="agent-create-model-select"
              disabled={submitting || modelOptions.length === 0}
              className={`flex h-9 w-full rounded-md border border-input bg-background px-3 text-sm shadow-sm focus-visible:outline-none focus-visible:ring-1 focus-visible:ring-ring disabled:cursor-not-allowed disabled:opacity-50${
                form.modelId === ""
                  ? " italic text-muted-foreground"
                  : ""
              }`}
            >
              <option value="">
                {inheritedValue("modelId") !== null
                  ? `inherited from ${inheritSourceLabel}: ${inheritedValue("modelId")}`
                  : `inherited from ${inheritSourceLabel}`}
              </option>
              {modelOptions.map((m) => (
                <option key={m.id} value={m.id}>
                  {m.label}
                </option>
              ))}
            </select>
          </InheritableField>

          {activeProviderId.length > 0 && (
            <AgentCreateCredentialStatus
              providerId={activeProviderId}
              credential={credentialDescriptor}
            />
          )}

          {/* Hosting — `hosting` field. Agent-exclusive (no unit-side
              counterpart) so the inherited value falls back to platform
              defaults at dispatch. */}
          <InheritableField
            label="Hosting"
            help="Agent lifecycle — ephemeral launches per-message; persistent runs continuously."
            isInherited={form.hosting === ""}
            inheritSourceLabel={inheritSourceLabel}
            inheritedValue={inheritedValue("hosting")}
            onClear={
              form.hosting !== ""
                ? () => {
                    setForm((prev) => ({ ...prev, hosting: "" }));
                    clearSubmitFeedback();
                  }
                : undefined
            }
            disabled={submitting}
          >
            <select
              value={form.hosting}
              onChange={(e) => {
                setForm((prev) => ({
                  ...prev,
                  hosting: e.target.value as HostingMode | "",
                }));
                clearSubmitFeedback();
              }}
              aria-label="Hosting mode"
              data-testid="agent-create-hosting-select"
              disabled={submitting}
              className={`flex h-9 w-full rounded-md border border-input bg-background px-3 text-sm shadow-sm focus-visible:outline-none focus-visible:ring-1 focus-visible:ring-ring disabled:cursor-not-allowed disabled:opacity-50${
                form.hosting === ""
                  ? " italic text-muted-foreground"
                  : ""
              }`}
            >
              <option value="">
                {inheritedValue("hosting") !== null
                  ? `inherited from ${inheritSourceLabel}: ${inheritedValue("hosting")}`
                  : `inherited from ${inheritSourceLabel}`}
              </option>
              {HOSTING_MODES.map((m) => (
                <option key={m.id} value={m.id}>
                  {m.label}
                </option>
              ))}
            </select>
          </InheritableField>
        </CardContent>
      </Card>

      {/* ── Unit assignment ───────────────────────────────────────── */}
      <Card className="mt-4">
        <CardHeader>
          <CardTitle>Unit assignment</CardTitle>
        </CardHeader>
        <CardContent className="space-y-3">
          <p className="text-xs text-muted-foreground">
            Assign the agent to one or more units, or leave this empty to
            create a top-level tenant-parented agent. Mirrors{" "}
            <code className="font-mono">--unit</code>.
          </p>

          {unitsQuery.isPending ? (
            <p className="text-sm text-muted-foreground">Loading units…</p>
          ) : unitsQuery.isError ? (
            <ApiErrorMessage error={unitsQuery.error} />
          ) : (unitsQuery.data ?? []).length === 0 ? (
            <p className="rounded-md border border-border bg-muted/30 px-3 py-2 text-sm text-muted-foreground">
              No units exist yet. The agent will be created at tenant level.
              Create a unit from{" "}
              <Link className="underline" href="/units/create">
                /units/create
              </Link>{" "}
              when you want a parent unit.
            </p>
          ) : (
            <fieldset
              className="grid grid-cols-1 gap-2 sm:grid-cols-2"
              aria-label="Initial unit assignment"
            >
              {(unitsQuery.data ?? []).map((unit) => {
                const checked =
                  form.unitIds.includes(unit.name) ||
                  form.unitIds.includes(unit.id);
                return (
                  <label
                    key={unit.name}
                    className="flex cursor-pointer items-start gap-2 rounded-md border border-border bg-background p-2 text-sm hover:bg-accent/40"
                  >
                    <input
                      type="checkbox"
                      className="mt-0.5"
                      checked={checked}
                      onChange={(e) => {
                        setForm((prev) => {
                          const next = e.target.checked
                            ? [
                                ...prev.unitIds.filter(
                                  (n) => n !== unit.name && n !== unit.id,
                                ),
                                unit.name,
                              ]
                            : prev.unitIds.filter(
                                (n) => n !== unit.name && n !== unit.id,
                              );
                          return { ...prev, unitIds: next };
                        });
                        clearSubmitFeedback();
                      }}
                      disabled={submitting}
                      aria-label={`Assign to ${unit.displayName || unit.name}`}
                    />
                    <span className="flex flex-col">
                      <span className="font-medium">
                        {unit.displayName || unit.name}
                      </span>
                      <span className="font-mono text-xs text-muted-foreground">
                        unit://{unit.name}
                      </span>
                    </span>
                  </label>
                );
              })}
            </fieldset>
          )}
        </CardContent>
      </Card>

      {/* ── Create progress ───────────────────────────────────────── */}
      {create.phase === "creating" && (
        <div
          aria-live="polite"
          className="mt-4 rounded-md border border-border bg-muted/30 px-3 py-2 text-sm text-muted-foreground"
          data-testid="agent-create-progress"
        >
          {source === "from-package" ? "Installing package…" : "Creating agent…"}
        </div>
      )}

      {create.phase === "done" && (
        <div
          role="status"
          className="mt-4 flex items-start gap-2 rounded-md border border-emerald-500/40 bg-emerald-500/10 px-3 py-2 text-sm text-emerald-900 dark:text-emerald-200"
          data-testid="agent-create-success"
        >
          <CheckCircle2
            className="mt-0.5 h-4 w-4 shrink-0"
            aria-hidden
          />
          <div className="min-w-0 flex-1">
            <p className="font-medium">
              {successMessageForVariant(create.successVariant)}
            </p>
            {create.installId && (
              <p className="truncate font-mono text-xs text-muted-foreground">
                {create.installId}
              </p>
            )}
          </div>
        </div>
      )}

      {/* ── Validation / submit errors ────────────────────────────── */}
      {/* Mutually exclusive: clearSubmitFeedback() resets both before
          runCreate(), so the shared `agent-create-error` testid never
          matches two elements at once. */}
      {validationMessage ? (
        <p
          role="alert"
          className="mt-4 rounded-md border border-destructive/50 bg-destructive/10 px-3 py-2 text-sm text-destructive"
          data-testid="agent-create-error"
        >
          {validationMessage}
        </p>
      ) : create.error !== null ? (
        <div className="mt-4" data-testid="agent-create-error">
          <ApiErrorMessage error={create.error} />
        </div>
      ) : null}

      {/* ── Multi-parent inheritance conflict (ADR-0039 §6 / I6) ─── */}
      {create.multiParentConflict !== null && (
        <MultiParentConflictBlock
          conflict={create.multiParentConflict}
          units={unitsQuery.data ?? []}
        />
      )}

      {/* ── Actions ───────────────────────────────────────────────── */}
      <div className="mt-6 flex flex-wrap items-center justify-end gap-3">
        {/* #2246: activate-after-create preference. Always shown; for
            ephemeral agents the deploy call is skipped (they activate on
            first message). Hidden while create is in flight. */}
        {create.phase !== "creating" && create.phase !== "done" && (
          <label
            className="flex cursor-pointer items-center gap-2 text-sm"
            htmlFor="auto-deploy-agent"
          >
            <input
              id="auto-deploy-agent"
              type="checkbox"
              checked={autoDeploy}
              onChange={(e) => setAutoDeploy(e.target.checked)}
              disabled={submitting}
              data-testid="agent-auto-deploy-checkbox"
            />
            Activate automatically after creation
          </label>
        )}
        <Button
          type="button"
          variant="outline"
          onClick={handleCancel}
          disabled={submitting}
        >
          Cancel
        </Button>
        <Button
          type="submit"
          disabled={
            submitting ||
            // ADR-0039 I6: block submit until the operator either trims
            // the parent set or sets the conflicting field explicitly.
            // The block clears on any form-state change.
            create.multiParentConflict !== null
          }
          data-testid="agent-create-submit"
        >
          {phaseLabel[create.phase]}
        </Button>
      </div>
    </form>
  );
}

function PackageConnectorRequirementsPanel({
  packageName,
  selectedBindings,
  onBindingChange,
}: {
  packageName: string | null;
  selectedBindings: Record<string, string>;
  onBindingChange: (connectorType: string, bindingId: string) => void;
}) {
  const packageQuery = usePackage(packageName ?? "", {
    enabled: Boolean(packageName),
  });

  if (!packageName) return null;

  if (packageQuery.isPending) {
    return (
      <div
        role="status"
        aria-label="Loading connector requirements"
        data-testid="package-connector-requirements"
        className="rounded-md border border-border bg-muted/30 px-3 py-2"
      >
        <div className="flex items-start gap-2">
          <Skeleton className="mt-0.5 h-4 w-4 shrink-0 rounded-full" />
          <div className="flex-1 space-y-2">
            <Skeleton className="h-4 w-40" />
            <Skeleton className="h-3 w-64 max-w-full" />
          </div>
        </div>
      </div>
    );
  }

  if (packageQuery.isError) {
    const message = formatTranslatedError(packageQuery.error);
    return (
      <div
        role="alert"
        data-testid="package-connector-requirements"
        className="flex items-start gap-2 rounded-md border border-warning/50 bg-warning/15 px-3 py-2 text-sm text-foreground"
      >
        <AlertTriangle
          className="mt-0.5 h-4 w-4 shrink-0 text-warning"
          aria-hidden
        />
        <p className="flex-1">
          Could not load connector requirements: {message}
        </p>
      </div>
    );
  }

  const requirements = connectorRequirements(packageQuery.data);
  if (requirements.length === 0) return null;

  return (
    <div
      role="status"
      data-testid="package-connector-requirements"
      className="rounded-md border border-primary/40 bg-primary/10 px-3 py-2 text-sm text-foreground"
    >
      <div className="flex items-start gap-2">
        <Plug className="mt-0.5 h-4 w-4 shrink-0 text-primary" aria-hidden />
        <div className="min-w-0 flex-1">
          <p className="font-medium">Connector requirements</p>
          <p className="mt-0.5 text-xs text-muted-foreground">
            Choose an existing binding for each connector this package
            declares.
          </p>
          <ul className="mt-2 space-y-2">
            {requirements.map((connectorType) => (
              <li key={connectorType}>
                <PackageConnectorBindingPicker
                  connectorType={connectorType}
                  selectedBindingId={selectedBindings[connectorType] ?? ""}
                  onBindingChange={(bindingId) =>
                    onBindingChange(connectorType, bindingId)
                  }
                />
              </li>
            ))}
          </ul>
        </div>
      </div>
    </div>
  );
}

function AgentCreateCredentialStatus({
  providerId,
  credential,
}: {
  providerId: string;
  credential: RuntimeCredentialDescriptor | null;
}) {
  const { data, isPending, isError } = useProviderCredentialStatus(providerId, {
    authMethod: credential?.authMethod,
  });

  if (providerId !== "ollama" && credential === null) return null;
  if (isPending) return null;

  const label = credential?.label ?? providerDisplayName(providerId);

  if (isError || !data) {
    return (
      <p className="text-xs text-muted-foreground" role="status">
        Could not verify {label}.
      </p>
    );
  }

  if (data.resolvable) {
    const text =
      data.source === "unit"
        ? `${label}: set on unit`
        : data.source === "tenant"
          ? `${label}: inherited from tenant default`
          : providerId === "ollama"
            ? `${label} reachable`
            : `${label} resolvable`;

    return (
      <div
        role="status"
        data-testid="agent-create-credential-status"
        data-resolvable="true"
        data-source={data.source ?? ""}
        className="flex items-start gap-2 rounded-md border border-emerald-500/40 bg-emerald-500/10 px-3 py-2 text-sm text-emerald-900 dark:text-emerald-200"
      >
        <CheckCircle2 className="mt-0.5 h-4 w-4 shrink-0" aria-hidden />
        <span>{text}</span>
      </div>
    );
  }

  return (
    <div
      role="alert"
      data-testid="agent-create-credential-status"
      data-resolvable="false"
      className="flex items-start gap-2 rounded-md border border-warning/50 bg-warning/15 px-3 py-2 text-sm text-foreground"
    >
      <AlertTriangle className="mt-0.5 h-4 w-4 shrink-0 text-warning" aria-hidden />
      {providerId === "ollama" ? (
        <p>
          {data.suggestion ??
            "Ollama not reachable. Check that the Ollama server is running."}
        </p>
      ) : (
        <p>
          {label}: not configured. Configure the tenant default from
          Settings before dispatch if the parent unit does not supply a
          unit-scoped secret.
        </p>
      )}
    </div>
  );
}

function PackageConnectorBindingPicker({
  connectorType,
  selectedBindingId,
  onBindingChange,
}: {
  connectorType: string;
  selectedBindingId: string;
  onBindingChange: (bindingId: string) => void;
}) {
  const bindingsQuery = useConnectorBindings(connectorType, {
    staleTime: 30_000,
  });

  if (bindingsQuery.isPending) {
    return (
      <div className="rounded-md border border-border bg-background/50 px-3 py-2">
        <div className="flex items-center justify-between gap-3">
          <span className="font-mono text-xs">{connectorType}</span>
          <Skeleton className="h-8 w-44" />
        </div>
      </div>
    );
  }

  if (bindingsQuery.isError) {
    const message = formatTranslatedError(bindingsQuery.error);
    return (
      <div
        role="alert"
        className="rounded-md border border-warning/50 bg-warning/10 px-3 py-2 text-xs"
      >
        <span className="font-mono">{connectorType}</span>
        <span className="ml-2 text-foreground">
          Could not load bindings: {message}
        </span>
      </div>
    );
  }

  const bindings = bindingsQuery.data ?? [];

  return (
    <label className="block rounded-md border border-border bg-background/50 px-3 py-2">
      <span className="flex items-center justify-between gap-3">
        <span className="font-mono text-xs">{connectorType}</span>
        {bindings.length === 0 && (
          <span className="text-xs text-muted-foreground">
            No bindings available.
          </span>
        )}
      </span>
      {bindings.length > 0 && (
        <select
          value={selectedBindingId}
          onChange={(e) => onBindingChange(e.target.value)}
          aria-label={`Binding for ${connectorType}`}
          data-testid={`package-connector-binding-${connectorType}`}
          className="mt-2 flex h-9 w-full rounded-md border border-input bg-background px-3 text-sm shadow-sm focus-visible:outline-none focus-visible:ring-1 focus-visible:ring-ring"
        >
          <option value="">Choose a binding</option>
          {bindings.map((binding) => (
            <option key={binding.unitId} value={binding.unitId}>
              {binding.unitDisplayName || binding.unitName} - unit://
              {binding.unitName}
            </option>
          ))}
        </select>
      )}
    </label>
  );
}

function connectorRequirements(pkg: PackageDetail | null | undefined): string[] {
  const seen = new Set<string>();
  const requirements: string[] = [];
  for (const declaration of pkg?.connectorDeclarations ?? []) {
    const connectorType = declaration.type.trim();
    if (!connectorType || declaration.required === false) continue;
    if (seen.has(connectorType)) continue;
    seen.add(connectorType);
    requirements.push(connectorType);
  }
  return requirements;
}

function successVariantForPackage(
  pkg: PackageDetail | null | undefined,
): SuccessVariant {
  if (!pkg) return "installed";
  return packageDeclaresUnits(pkg) ? "unit" : "agent";
}

function packageDeclaresUnits(pkg: PackageDetail): boolean {
  const currentUnitTemplates = pkg.unitTemplates ?? [];
  if (currentUnitTemplates.length > 0) return true;

  const legacyUnits = (pkg as PackageDetail & { units?: unknown }).units;
  if (Array.isArray(legacyUnits)) return legacyUnits.length > 0;
  if (legacyUnits && typeof legacyUnits === "object") {
    return Object.keys(legacyUnits).length > 0;
  }
  return false;
}

function successMessageForVariant(
  variant: SuccessVariant | null,
): string {
  switch (variant) {
    case "unit":
      return "Unit installed successfully.";
    case "installed":
      return "Installed successfully.";
    case "agent":
    default:
      return "Agent created successfully.";
  }
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
  testId: string;
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
        <span className="text-sm font-medium">{title}</span>
        <p className="text-xs text-muted-foreground">{description}</p>
      </div>
    </button>
  );
}

// InheritableField — DESIGN.md §12.6 affordance (ADR-0039 I4)
// ---------------------------------------------------------------------------

interface InheritableFieldProps {
  /** Field label rendered above the control. */
  label: string;
  /** Default help copy shown when the operator has set an explicit value. */
  help: React.ReactNode;
  /** True when the field is currently empty (inheriting from parent). */
  isInherited: boolean;
  /** Display name of the inherit source (unit display name, or "tenant defaults"). */
  inheritSourceLabel: string;
  /** Resolved parent value, when known, for the `: <value>` suffix. */
  inheritedValue: string | null;
  /**
   * Optional clear handler. When supplied (and the field has an explicit
   * value) a small "Use inherited value" button appears next to the label
   * so the operator can revert without erasing into a long string.
   */
  onClear?: () => void;
  disabled?: boolean;
  children: React.ReactNode;
}

/**
 * Per-field inherit affordance per DESIGN.md §12.6 — italic placeholder
 * inside the control plus a help-copy line below carrying
 * `data-testid="inherit-indicator"` when the field is in inherit mode.
 * When the operator has set an explicit value the indicator is hidden
 * and the regular `help` copy renders instead.
 */
function InheritableField({
  label,
  help,
  isInherited,
  inheritSourceLabel,
  inheritedValue,
  onClear,
  disabled,
  children,
}: InheritableFieldProps) {
  const indicatorText = inheritedValue
    ? `inherited from ${inheritSourceLabel}: ${inheritedValue}`
    : `inherited from ${inheritSourceLabel}`;

  return (
    <div className="space-y-1">
      <div className="flex items-center justify-between gap-2">
        <span className="text-sm text-muted-foreground">{label}</span>
        {!isInherited && onClear && (
          <Button
            type="button"
            size="sm"
            variant="ghost"
            onClick={onClear}
            disabled={disabled}
            className="h-7 px-2 text-xs"
            aria-label={`Use inherited ${label.toLowerCase()}`}
            data-testid={`agent-create-inherit-${label
              .toLowerCase()
              .replace(/\s+/g, "-")}`}
          >
            Use inherited value
          </Button>
        )}
      </div>
      {children}
      {isInherited ? (
        <p
          className="text-xs italic text-muted-foreground"
          data-testid="inherit-indicator"
        >
          {indicatorText}
        </p>
      ) : (
        <p className="text-xs text-muted-foreground">{help}</p>
      )}
    </div>
  );
}

// ---------------------------------------------------------------------------
// MultiParentConflictBlock — ADR-0039 §6 / I6
// ---------------------------------------------------------------------------

/**
 * Inline error block rendered when create returns the structured 422
 * `MultiParentInheritanceConflict`. Lists each diverging
 * field and the parent-attributed values so the operator can see at a
 * glance which parents disagree and pick a resolution path.
 *
 * Visual: destructive-palette banner per `DESIGN.md` §12.4 (alert
 * banners) — same shape as the validation-panel error state in
 * §12.8 so a single axe pass covers both surfaces.
 *
 * Attribution: when a unit listed in the conflict is also in the unit
 * picker (`unitsQuery.data`) we show its display name in a "verbose"
 * line; the canonical 32-character hex id is always shown alongside
 * for log correlation. We tolerate hyphenated GUIDs from the units
 * endpoint by normalising both sides to the canonical form before
 * matching (`canonicalUnitId`).
 */
function MultiParentConflictBlock({
  conflict,
  units,
}: {
  conflict: MultiParentInheritanceConflict;
  units: ReadonlyArray<UnitResponse>;
}) {
  // Pre-build a {canonical-id → unit} index so each parent value can
  // resolve a friendly name in O(1).
  const unitIndex = new Map<string, UnitResponse>();
  for (const u of units) {
    const canonical = canonicalUnitId(String(u.id));
    if (canonical !== null) unitIndex.set(canonical, u);
  }

  const describeParent = (rawUnitId: string): string => {
    const canonical = canonicalUnitId(rawUnitId) ?? rawUnitId;
    const unit = unitIndex.get(canonical);
    if (unit) return unit.displayName || unit.name;
    return rawUnitId;
  };

  return (
    <div
      role="alert"
      className="mt-4 space-y-3 rounded-md border border-destructive/50 bg-destructive/10 px-3 py-3 text-sm text-foreground"
      data-testid="multi-parent-inheritance-conflict"
    >
      <div className="flex items-start gap-2">
        <AlertTriangle
          className="mt-0.5 h-4 w-4 shrink-0 text-destructive"
          aria-hidden
        />
        <div className="flex-1 space-y-1">
          <p className="font-medium text-destructive">
            Parent units disagree on inherited execution config
          </p>
          <p className="text-xs text-foreground">
            The agent is inheriting one or more execution-config fields,
            but the selected parent units contribute conflicting values.
            Either remove a conflicting parent or set the field explicitly
            on the agent before re-submitting.
          </p>
        </div>
      </div>

      <ul className="space-y-2">
        {conflict.fields.map((row) => (
          <li
            key={row.field}
            className="rounded-md border border-destructive/30 bg-background/50 px-3 py-2"
            data-testid={`multi-parent-inheritance-conflict-field-${row.field}`}
          >
            <p className="font-mono text-xs font-medium text-destructive">
              {row.field}
            </p>
            <ul className="mt-1 space-y-1">
              {row.values.map((v, idx) => (
                <li
                  key={`${v.unitId}-${idx}`}
                  className="flex flex-wrap items-baseline gap-x-2 text-xs"
                >
                  <span className="font-medium">{describeParent(v.unitId)}</span>
                  <span className="font-mono text-muted-foreground">
                    {v.unitId}
                  </span>
                  <span aria-hidden className="text-muted-foreground">
                    →
                  </span>
                  <span className="font-mono">{v.value}</span>
                </li>
              ))}
            </ul>
          </li>
        ))}
      </ul>
    </div>
  );
}
