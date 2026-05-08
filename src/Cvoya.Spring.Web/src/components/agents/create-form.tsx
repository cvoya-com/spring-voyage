"use client";

import { useCallback, useMemo, useRef, useState } from "react";
import { useQuery, useQueryClient } from "@tanstack/react-query";
import Link from "next/link";
import { AlertTriangle } from "lucide-react";

import { Badge } from "@/components/ui/badge";
import { Button } from "@/components/ui/button";
import {
  Card,
  CardContent,
  CardHeader,
  CardTitle,
} from "@/components/ui/card";
import { Input } from "@/components/ui/input";
import { useToast } from "@/components/ui/toast";
import { api, ApiError } from "@/lib/api/client";
import {
  useModelProviderModels,
  useModelProviders,
  useUnitExecution,
} from "@/lib/api/queries";
import { queryKeys } from "@/lib/api/query-keys";
import { AGENT_NAME_PATTERN } from "@/lib/agents/create-agent";
import {
  canonicalUnitId,
  parseMultiParentInheritanceConflict,
  type MultiParentInheritanceConflict,
} from "@/lib/agents/multi-parent-conflict";
import {
  buildAgentPackageYaml,
  type AgentPackageFormState,
} from "@/app/agents/create/build-agent-package";
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
import type { UnitResponse } from "@/lib/api/types";

// ---------------------------------------------------------------------------
// Types
// ---------------------------------------------------------------------------

/**
 * Internal form state owned by `<AgentCreateForm>`. Mirrors the wire-level
 * AgentPackage shape (`build-agent-package.ts`) so the runtime / model /
 * image fields land on disk byte-for-byte the same as a CLI
 * `spring agent create`.
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

type SubmitPhase =
  | "idle"
  | "installing"       // POST /api/v1/packages/install/file in flight
  | "polling"          // polling GET /api/v1/installs/{id}
  | "memberships"      // post-install membership-add loop
  | "done"
  | "failed"
  | "install-failed";  // Phase-2 failure; retry/abort available

interface InstallState {
  installId: string | null;
  agentId: string | null;
  phase: SubmitPhase;
  error: string | null;
  /** Membership-add failures: unitId → error message */
  membershipErrors: Record<string, string>;
  /** Which unit memberships succeeded so far */
  membershipDone: string[];
  /**
   * ADR-0039 §6 (I6): structured 422 from a membership-add call. When
   * non-null, the form renders an inline conflict block listing every
   * diverging field and the parent-attributed values, and blocks the
   * submit button until the operator either trims the parent set or
   * sets the field explicitly. Cleared on any form-state change.
   */
  multiParentConflict: MultiParentInheritanceConflict | null;
}

const INITIAL_INSTALL: InstallState = {
  installId: null,
  agentId: null,
  phase: "idle",
  error: null,
  membershipErrors: {},
  membershipDone: [],
  multiParentConflict: null,
};

const POLL_INTERVAL_MS = 2_000;
const POLL_TIMEOUT_MS = 120_000;

// ---------------------------------------------------------------------------
// Props
// ---------------------------------------------------------------------------

/**
 * Successful-create payload surfaced to the caller. `agentId` is the
 * URL-safe id the operator chose; `unitIds` is the (possibly empty)
 * post-install membership set the form actually wired.
 */
export interface AgentCreateSuccess {
  agentId: string;
  unitIds: string[];
}

export interface AgentCreateFormProps {
  /**
   * Optional initial unit ids (URL-safe names). Useful for the
   * unit-tab dialog (J1) where the dialog opens "from" a specific unit
   * and the assignment should default to it. Empty by default — the
   * standalone page does not pre-select anything.
   */
  initialUnitIds?: string[];
  /**
   * Called after the install reaches active AND every requested
   * membership succeeds. The standalone page navigates to
   * `/units?node=<first>&tab=Agents`; a dialog caller might close
   * itself instead. Receives the successful-create summary.
   */
  onSuccess?: (result: AgentCreateSuccess) => void;
  /**
   * Called when the operator clicks Cancel / Back. The standalone page
   * uses this to call `router.back()`; a dialog caller closes itself.
   * When omitted, the cancel/back buttons stay visible but no-op.
   */
  onCancel?: () => void;
}

// ---------------------------------------------------------------------------
// Component
// ---------------------------------------------------------------------------

/**
 * Reusable agent-create form. Owns identity, execution, and unit-assignment
 * fields plus the two-phase install flow (POST /api/v1/packages/install/file,
 * poll /api/v1/installs/{id}, sequential membership wiring). Extracted from
 * `app/agents/create/page.tsx` (ADR-0039 I3) so the unit-tab dialog (J1) can
 * embed the same form without forking the install flow or the wire-format
 * helpers.
 *
 * Visual chrome reuses the existing `<Card>` / `<Input>` / `<Button>`
 * primitives — DESIGN.md does not need an update for this extraction.
 */
export function AgentCreateForm({
  initialUnitIds = [],
  onSuccess,
  onCancel,
}: AgentCreateFormProps) {
  const queryClient = useQueryClient();
  const { toast } = useToast();

  const initialForm = useMemo<FormState>(
    () => ({
      id: "",
      displayName: "",
      role: "",
      description: "",
      // ADR-0039 I4 / DESIGN.md §12.6: default every execution field to
      // inherit-mode (empty). The placeholder + help-copy below the
      // field shows the resolved parent value so the operator sees what
      // they will inherit; an explicit pick overrides it.
      runtime: "",
      modelProviderId: "",
      modelId: "",
      image: "",
      hosting: "",
      unitIds: [...initialUnitIds],
    }),
    [initialUnitIds],
  );

  const [form, setForm] = useState<FormState>(initialForm);
  const [validationMessage, setValidationMessage] = useState<string | null>(null);
  const [install, setInstall] = useState<InstallState>(INITIAL_INSTALL);

  // Abort controller for the polling loop so Back/Cancel stops it.
  const pollAbortRef = useRef<AbortController | null>(null);

  // ── Form helpers ───────────────────────────────────────────────────────

  const update = <K extends keyof FormState>(key: K, value: FormState[K]) => {
    setForm((prev) => ({ ...prev, [key]: value }));
    setValidationMessage(null);
    if (install.phase === "idle" || install.phase === "failed") {
      setInstall(INITIAL_INSTALL);
    }
    // ADR-0039 I6: any form-state change invalidates a stale conflict
    // block so the submit button re-enables once the operator has
    // resolved the divergence (trimmed the parent set or set the
    // conflicting field explicitly).
    if (install.multiParentConflict !== null) {
      setInstall((prev) => ({ ...prev, multiParentConflict: null }));
    }
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

  // ADR-0039 I4: when `form.runtime === ""` the operator wants to inherit;
  // gating logic for the provider/model dropdowns still needs to resolve
  // against *some* runtime. Use the inherited (or platform default)
  // runtime as the effective key for those decisions.
  const inheritedRuntimeFallback: RuntimeId = DEFAULT_RUNTIME_ID;
  const effectiveRuntime: RuntimeId =
    (form.runtime || inheritedRuntimeFallback) as RuntimeId;

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
  const modelsQuery = useModelProviderModels(activeProviderId, {
    enabled: Boolean(activeProviderId),
  });

  // ── Inherit-from-parent context (ADR-0039 I4) ──────────────────────────
  //
  // The "inherited value" we surface in the per-field placeholder + help
  // copy depends on the selected unit set:
  //   * 0 units → tenant defaults; we don't have a tenant-defaults endpoint
  //     yet so fall back to platform defaults (claude-code, etc.) and
  //     name the parent "tenant defaults".
  //   * 1 unit → that unit's own resolved execution block.
  //   * >1 units → DESIGN.md §12.6 validation work (multi-parent conflict)
  //     is a separate task; for I4 we surface the first selected unit
  //     and rely on backend validation to reject conflicting parents at
  //     install time.
  //
  // We load `useUnitExecution` for the first selected unit so the values
  // surface as soon as the operator ticks a unit checkbox.
  const selectedUnitName = form.unitIds[0] ?? null;
  const selectedUnit = useMemo(
    () =>
      (unitsQuery.data ?? []).find((u) => u.name === selectedUnitName) ?? null,
    [unitsQuery.data, selectedUnitName],
  );
  const selectedUnitExecutionQuery = useUnitExecution(
    selectedUnitName ?? "",
    { enabled: Boolean(selectedUnitName) },
  );

  /** Display name for the inherit-source — unit name, or "tenant defaults". */
  const inheritSourceLabel: string =
    selectedUnit?.displayName?.trim() ||
    selectedUnit?.name ||
    "tenant defaults";

  /**
   * Resolve the inherited value for one execution slot. Returns `null`
   * when there is nothing to surface (the platform-side default is not
   * known on the client) so the caller can fall back to a generic copy.
   */
  const inheritedValue = useCallback(
    (slot: "runtime" | "modelProvider" | "modelId" | "image" | "hosting"): string | null => {
      const unitDefaults = selectedUnitExecutionQuery.data ?? null;
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
    [selectedUnitName, selectedUnitExecutionQuery.data],
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

  // ── Install flow ───────────────────────────────────────────────────────

  /**
   * Polls GET /api/v1/installs/{id} every POLL_INTERVAL_MS until the
   * status reaches a terminal state (`active` or `failed`), or until the
   * abort signal fires, or until POLL_TIMEOUT_MS elapses.
   */
  const pollUntilTerminal = useCallback(
    async (installId: string, signal: AbortSignal): Promise<"active" | "failed" | "aborted"> => {
      const deadline = Date.now() + POLL_TIMEOUT_MS;
      while (Date.now() < deadline) {
        if (signal.aborted) return "aborted";

        await new Promise<void>((resolve) => {
          const t = setTimeout(resolve, POLL_INTERVAL_MS);
          signal.addEventListener("abort", () => { clearTimeout(t); resolve(); }, { once: true });
        });

        if (signal.aborted) return "aborted";

        const status = await api.getInstallStatus(installId);
        if (status === null) return "failed"; // install no longer exists
        if (status.status === "active") return "active";
        if (status.status === "failed") return "failed";
        // still "staging" — keep polling
      }
      return "failed"; // timed out
    },
    [],
  );

  /**
   * Post-install membership wiring. Fires `assignUnitAgent` for each
   * selected unit sequentially. Updates install.membershipErrors /
   * membershipDone in real time.
   *
   * ADR-0039 §6 (I6): when a membership-add returns the structured 422
   * `MultiParentInheritanceConflict`, surface it on `multiParentConflict`
   * for inline rendering (`<MultiParentConflictBlock>`) instead of
   * dumping the formatted ApiError message into `membershipErrors`.
   * The conflict short-circuits the rest of the loop because every
   * subsequent unit would report the same divergence.
   */
  const addMemberships = useCallback(
    async (agentId: string, unitIds: string[]) => {
      const errors: Record<string, string> = {};
      const done: string[] = [];
      let conflict: MultiParentInheritanceConflict | null = null;
      for (const unitId of unitIds) {
        try {
          await api.assignUnitAgent(unitId, agentId);
          done.push(unitId);
        } catch (err) {
          if (err instanceof ApiError && err.status === 422) {
            const parsed = parseMultiParentInheritanceConflict(err.body);
            if (parsed !== null) {
              conflict = parsed;
              setInstall((prev) => ({
                ...prev,
                membershipErrors: { ...errors },
                membershipDone: [...done],
                multiParentConflict: parsed,
              }));
              // Stop on the first conflict — every remaining unit would
              // hit the same divergence and we already have the full
              // per-field, per-parent picture from the resolver.
              return { errors, done, conflict };
            }
          }
          const msg = err instanceof Error ? err.message : String(err);
          errors[unitId] = msg;
        }
        setInstall((prev) => ({
          ...prev,
          membershipErrors: { ...errors },
          membershipDone: [...done],
        }));
      }
      return { errors, done, conflict };
    },
    [],
  );

  const invalidateAgentCaches = useCallback(() => {
    queryClient.invalidateQueries({ queryKey: queryKeys.agents.all });
    queryClient.invalidateQueries({ queryKey: queryKeys.units.all });
    queryClient.invalidateQueries({ queryKey: queryKeys.dashboard.all });
    queryClient.invalidateQueries({ queryKey: queryKeys.tenant.tree() });
  }, [queryClient]);

  const runInstall = useCallback(async () => {
    // Abort any in-flight poll from a previous attempt.
    pollAbortRef.current?.abort();
    const ac = new AbortController();
    pollAbortRef.current = ac;

    const agentId = form.id.trim();
    const unitIds = form.unitIds.filter((u) => u.trim().length > 0);

    // Phase: installing (POST /api/v1/packages/install/file)
    setInstall({
      installId: null,
      agentId,
      phase: "installing",
      error: null,
      membershipErrors: {},
      membershipDone: [],
      multiParentConflict: null,
    });

    let installId: string;
    let alreadyActive = false;
    try {
      // ADR-0039 I4: blank fields submit as undefined so the backend
      // resolves the parent unit's value (or tenant default) at dispatch.
      // When the operator picked a fixed-provider runtime explicitly,
      // snap modelProvider to the runtime's fixed value.
      const submittedProvider =
        form.runtime !== "" && fixedProviderId !== null
          ? fixedProviderId
          : form.modelProviderId;
      const yamlInput: AgentPackageFormState = {
        id: agentId,
        displayName: form.displayName.trim(),
        role: form.role.trim() || undefined,
        description: form.description.trim() || undefined,
        image: form.image.trim() || undefined,
        hosting: form.hosting || undefined,
        // ADR-0038: emit `ai.runtime` + structured
        // `ai.model: {provider, id}`. The legacy `ai.agent` /
        // `ai.tool` shape is rejected by the manifest parser.
        runtime: form.runtime || undefined,
        modelProvider: submittedProvider.trim() || undefined,
        modelId: form.modelId.trim() || undefined,
        unitIds,
      };
      const yaml = buildAgentPackageYaml(yamlInput);
      const resp = await api.installPackageFile(yaml);
      installId = resp.installId;

      if (resp.status === "failed") {
        const pkgErr = resp.packages.find((p) => p.state === "failed")?.errorMessage;
        setInstall((prev) => ({
          ...prev,
          installId,
          phase: "install-failed",
          error: pkgErr ?? "Install failed.",
        }));
        return;
      }

      alreadyActive = resp.status === "active";
    } catch (err) {
      const msg = err instanceof Error ? err.message : String(err);
      setInstall((prev) => ({ ...prev, phase: "failed", error: msg }));
      toast({
        title: "Install failed",
        description: msg,
        variant: "destructive",
      });
      return;
    }

    // Phase: polling (only if not already active from the initial response)
    if (!alreadyActive) {
      setInstall((prev) => ({ ...prev, installId, phase: "polling" }));

      const terminal = await pollUntilTerminal(installId, ac.signal);
      if (ac.signal.aborted) {
        setInstall(INITIAL_INSTALL);
        return;
      }

      if (terminal === "failed") {
        setInstall((prev) => ({
          ...prev,
          phase: "install-failed",
          error: "Install did not reach active state. Check the install log.",
        }));
        return;
      }
    }

    // Phase: memberships
    if (unitIds.length > 0) {
      setInstall((prev) => ({ ...prev, phase: "memberships" }));
      const { errors, conflict } = await addMemberships(agentId, unitIds);

      // ADR-0039 §6 (I6): the structured 422 path. The agent is
      // installed but the parent set diverges on an inherited field;
      // the operator must trim the set or set the field explicitly
      // before any membership row is written.
      if (conflict !== null) {
        setInstall((prev) => ({
          ...prev,
          phase: "failed",
          error: null,
          multiParentConflict: conflict,
        }));
        queryClient.invalidateQueries({ queryKey: queryKeys.agents.all });
        queryClient.invalidateQueries({ queryKey: queryKeys.tenant.tree() });
        toast({
          title: "Agent installed (membership blocked)",
          description:
            "Parent units disagree on an inherited execution-config field.",
          variant: "destructive",
        });
        return;
      }

      if (Object.keys(errors).length > 0) {
        // Partial success — agent installed but some memberships failed.
        const failedUnits = Object.keys(errors).join(", ");
        setInstall((prev) => ({
          ...prev,
          phase: "failed",
          error: `Agent installed. Membership in ${failedUnits} could not be added — see details above.`,
        }));
        // Still invalidate caches for the agent.
        queryClient.invalidateQueries({ queryKey: queryKeys.agents.all });
        queryClient.invalidateQueries({ queryKey: queryKeys.tenant.tree() });
        toast({
          title: "Agent installed (partial)",
          description: `Membership add failed for: ${failedUnits}`,
          variant: "destructive",
        });
        return;
      }
    }

    // Phase: done
    setInstall((prev) => ({ ...prev, phase: "done" }));
    invalidateAgentCaches();

    toast({ title: "Agent created", description: agentId });
    onSuccess?.({ agentId, unitIds });
  }, [
    form,
    fixedProviderId,
    addMemberships,
    pollUntilTerminal,
    queryClient,
    invalidateAgentCaches,
    onSuccess,
    toast,
  ]);

  const handleSubmit = (e: React.FormEvent<HTMLFormElement>) => {
    e.preventDefault();
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
    const unitIds = form.unitIds.filter((u) => u.trim().length > 0);
    if (unitIds.length === 0) {
      setValidationMessage("Pick at least one unit to assign the agent to.");
      return;
    }
    setValidationMessage(null);
    void runInstall();
  };

  const handleRetry = async () => {
    if (!install.installId) return;
    try {
      setInstall((prev) => ({ ...prev, phase: "installing", error: null }));
      const resp = await api.retryInstall(install.installId);
      if (resp.status === "active") {
        // Complete immediately
        if (form.unitIds.length > 0) {
          setInstall((prev) => ({ ...prev, phase: "memberships" }));
          const { errors, conflict } = await addMemberships(
            install.agentId!,
            form.unitIds,
          );
          if (conflict !== null) {
            setInstall((prev) => ({
              ...prev,
              phase: "failed",
              error: null,
              multiParentConflict: conflict,
            }));
            return;
          }
          if (Object.keys(errors).length > 0) {
            const failedUnits = Object.keys(errors).join(", ");
            setInstall((prev) => ({
              ...prev,
              phase: "failed",
              error: `Agent installed. Membership in ${failedUnits} could not be added.`,
            }));
            return;
          }
        }
        setInstall((prev) => ({ ...prev, phase: "done" }));
        invalidateAgentCaches();
        const agentId = install.agentId ?? "";
        toast({ title: "Agent created", description: agentId });
        onSuccess?.({ agentId, unitIds: form.unitIds });
      } else if (resp.status === "failed") {
        const pkgErr = resp.packages.find((p) => p.state === "failed")?.errorMessage;
        setInstall((prev) => ({
          ...prev,
          phase: "install-failed",
          error: pkgErr ?? "Retry failed.",
        }));
      } else {
        // Back to polling
        const ac = new AbortController();
        pollAbortRef.current = ac;
        setInstall((prev) => ({ ...prev, phase: "polling" }));
        const terminal = await pollUntilTerminal(install.installId!, ac.signal);
        if (terminal !== "active") {
          setInstall((prev) => ({
            ...prev,
            phase: "install-failed",
            error: "Retry did not reach active state.",
          }));
        }
      }
    } catch (err) {
      const msg = err instanceof Error ? err.message : String(err);
      setInstall((prev) => ({ ...prev, phase: "install-failed", error: msg }));
    }
  };

  const handleAbort = async () => {
    pollAbortRef.current?.abort();
    if (!install.installId) {
      setInstall(INITIAL_INSTALL);
      return;
    }
    try {
      await api.abortInstall(install.installId);
    } catch {
      // Ignore abort errors — we're discarding the install either way.
    }
    setInstall(INITIAL_INSTALL);
    toast({ title: "Install aborted", description: form.id });
  };

  const handleCancel = () => {
    pollAbortRef.current?.abort();
    onCancel?.();
  };

  // ── Derived UI state ───────────────────────────────────────────────────

  const submitting =
    install.phase === "installing" ||
    install.phase === "polling" ||
    install.phase === "memberships";

  // ADR-0039 I4 — flips the Execution card header badge between
  // `Inherits` (everything blank) and `Configured` (any explicit pick).
  const executionHasOverride =
    form.runtime !== "" ||
    form.modelProviderId !== "" ||
    form.modelId !== "" ||
    form.image !== "" ||
    form.hosting !== "";

  const phaseLabel: Record<SubmitPhase, string> = {
    idle: "Create agent",
    installing: "Installing…",
    polling: "Activating…",
    memberships: "Wiring memberships…",
    done: "Create agent",
    failed: "Create agent",
    "install-failed": "Create agent",
  };

  return (
    <form onSubmit={handleSubmit} noValidate>
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
              variant="default"
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
                ? () =>
                    setForm((prev) => ({
                      ...prev,
                      runtime: "",
                      modelId: "",
                    }))
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
                setValidationMessage(null);
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
                  ? () =>
                      setForm((prev) => ({
                        ...prev,
                        modelProviderId: "",
                        modelId: "",
                      }))
                  : undefined
              }
              disabled={submitting}
            >
              <select
                value={form.modelProviderId}
                onChange={(e) =>
                  setForm((prev) => ({
                    ...prev,
                    modelProviderId: e.target.value,
                    modelId: "",
                  }))
                }
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
                ? () => setForm((prev) => ({ ...prev, image: "" }))
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
                ? () => setForm((prev) => ({ ...prev, modelId: "" }))
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
                ? () => setForm((prev) => ({ ...prev, hosting: "" }))
                : undefined
            }
            disabled={submitting}
          >
            <select
              value={form.hosting}
              onChange={(e) =>
                setForm((prev) => ({
                  ...prev,
                  hosting: e.target.value as HostingMode | "",
                }))
              }
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
            Assign the agent to one or more units after it is installed.
            At least one unit is required. Memberships are wired as
            post-install side-effects. Mirrors{" "}
            <code className="font-mono">--unit</code>.
          </p>

          {unitsQuery.isPending ? (
            <p className="text-sm text-muted-foreground">Loading units…</p>
          ) : unitsQuery.isError ? (
            <p
              role="alert"
              className="rounded-md border border-destructive/50 bg-destructive/10 px-3 py-2 text-sm text-destructive"
            >
              Could not load units:{" "}
              {unitsQuery.error instanceof Error
                ? unitsQuery.error.message
                : String(unitsQuery.error)}
            </p>
          ) : (unitsQuery.data ?? []).length === 0 ? (
            <p className="rounded-md border border-border bg-muted/30 px-3 py-2 text-sm text-muted-foreground">
              No units exist yet. Create one from{" "}
              <Link className="underline" href="/units/create">
                /units/create
              </Link>{" "}
              first — agents must belong to a unit.
            </p>
          ) : (
            <fieldset
              className="grid grid-cols-1 gap-2 sm:grid-cols-2"
              aria-label="Initial unit assignment"
            >
              {(unitsQuery.data ?? []).map((unit) => {
                const checked = form.unitIds.includes(unit.name);
                const membershipOk = install.membershipDone.includes(unit.name);
                const membershipErr = install.membershipErrors[unit.name];
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
                            ? [...prev.unitIds, unit.name]
                            : prev.unitIds.filter((n) => n !== unit.name);
                          return { ...prev, unitIds: next };
                        });
                        setValidationMessage(null);
                        // ADR-0039 I6: trimming the parent set is one of
                        // the two operator-resolutions for a multi-parent
                        // conflict. Clear the inline error so the submit
                        // button re-enables.
                        if (install.multiParentConflict !== null) {
                          setInstall((prev) => ({
                            ...prev,
                            multiParentConflict: null,
                          }));
                        }
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
                      {membershipOk && (
                        <span className="text-xs text-success">
                          Membership added
                        </span>
                      )}
                      {membershipErr && (
                        <span className="text-xs text-destructive" role="alert">
                          Failed: {membershipErr}
                        </span>
                      )}
                    </span>
                  </label>
                );
              })}
            </fieldset>
          )}
        </CardContent>
      </Card>

      {/* ── Install progress ──────────────────────────────────────── */}
      {install.phase !== "idle" &&
        install.phase !== "failed" &&
        install.phase !== "install-failed" && (
          <div
            aria-live="polite"
            className="mt-4 rounded-md border border-border bg-muted/30 px-3 py-2 text-sm text-muted-foreground"
            data-testid="install-progress"
          >
            {install.phase === "installing" && "Submitting package…"}
            {install.phase === "polling" && "Waiting for install to become active…"}
            {install.phase === "memberships" && (
              <>
                Wiring unit memberships
                {install.membershipDone.length > 0 && (
                  <> ({install.membershipDone.length} / {form.unitIds.length} done)</>
                )}
                …
              </>
            )}
          </div>
        )}

      {/* ── Validation / submit errors ────────────────────────────── */}
      {(validationMessage || install.error) &&
        install.phase !== "install-failed" && (
          <p
            role="alert"
            className="mt-4 rounded-md border border-destructive/50 bg-destructive/10 px-3 py-2 text-sm text-destructive"
            data-testid="agent-create-error"
          >
            {validationMessage ?? install.error}
          </p>
        )}

      {/* ── Multi-parent inheritance conflict (ADR-0039 §6 / I6) ─── */}
      {install.multiParentConflict !== null && (
        <MultiParentConflictBlock
          conflict={install.multiParentConflict}
          units={unitsQuery.data ?? []}
        />
      )}

      {/* ── Phase-2 failure panel ─────────────────────────────────── */}
      {install.phase === "install-failed" && (
        <div
          role="alert"
          className="mt-4 space-y-3 rounded-md border border-destructive/50 bg-destructive/10 px-3 py-3 text-sm text-destructive"
          data-testid="install-failed-panel"
        >
          <p className="font-medium">Install failed</p>
          {install.error && (
            <p className="text-xs">{install.error}</p>
          )}
          {install.installId && (
            <p className="font-mono text-xs text-muted-foreground">
              Install id: {install.installId}
            </p>
          )}
          <div className="flex gap-2">
            <Button
              type="button"
              size="sm"
              variant="outline"
              onClick={() => void handleRetry()}
              disabled={submitting}
              data-testid="retry-button"
            >
              Retry
            </Button>
            <Button
              type="button"
              size="sm"
              variant="outline"
              onClick={() => void handleAbort()}
              disabled={submitting}
              data-testid="abort-button"
            >
              Abort install
            </Button>
          </div>
        </div>
      )}

      {/* ── Actions ───────────────────────────────────────────────── */}
      <div className="mt-6 flex items-center justify-end gap-2">
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
            install.phase === "install-failed" ||
            // ADR-0039 I6: block submit until the operator either trims
            // the parent set or sets the conflicting field explicitly.
            // The block clears on any form-state change.
            install.multiParentConflict !== null
          }
          data-testid="agent-create-submit"
        >
          {phaseLabel[install.phase]}
        </Button>
      </div>
    </form>
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
 * Inline error block rendered when a membership-add returns the
 * structured 422 `MultiParentInheritanceConflict`. Lists each diverging
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
