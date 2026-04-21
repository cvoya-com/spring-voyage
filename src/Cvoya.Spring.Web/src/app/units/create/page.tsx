"use client";

import { useCallback, useEffect, useMemo, useState } from "react";
import { useRouter } from "next/navigation";
import { useMutation, useQueryClient } from "@tanstack/react-query";
import {
  AlertTriangle,
  ArrowLeft,
  Check,
  CheckCircle2,
  ExternalLink,
  Eye,
  EyeOff,
  FileCode,
  FileText,
  KeyRound,
  Plug,
  Rocket,
  Sparkles,
} from "lucide-react";
// Note: unit validation is now backend-side (T-02/T-04). The wizard no
// longer tries to reach an LLM from the browser to check the key —
// POST /api/v1/units returns 201 immediately and the backend workflow
// drives `Draft → Validating → {Stopped | Error}`. The detail page's
// Validation panel (T-07) surfaces progress + structured errors via
// SSE. See GitHub issue #949.

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
  useAgentRuntimeModels,
  useAgentRuntimes,
  useConnectorTypes,
  useOllamaModels,
  useProviderCredentialStatus,
  useUnit,
  useUnitExecution,
  useUnitTemplates,
} from "@/lib/api/queries";
import { queryKeys } from "@/lib/api/query-keys";
import type {
  InstalledAgentRuntimeResponse,
  UnitConnectorBindingRequest,
  UnitStatus,
} from "@/lib/api/types";
import { EXECUTION_RUNTIMES } from "@/lib/api/types";
import ValidationPanel from "@/components/units/detail/validation-panel";
import {
  DEFAULT_EXECUTION_TOOL,
  DEFAULT_HOSTING_MODE,
  EXECUTION_TOOLS,
  HOSTING_MODES,
  getRuntimeSecretName,
  getToolRuntimeId,
  getToolWireProvider,
  type ExecutionTool,
  type HostingMode,
} from "@/lib/ai-models";
import { cn } from "@/lib/utils";

const DEFAULT_COLOR = "#6366f1";

const NAME_PATTERN = /^[a-z0-9-]+$/;

// Issue #661: the wizard splits into Identity (step 1) and Execution
// (step 2) — see the issue body for the field-level acceptance
// criteria. Subsequent screens (Mode, Connector, Secrets, Finalize)
// are unchanged.

type Step = 1 | 2 | 3 | 4 | 5 | 6;
type Mode = "template" | "scratch" | "yaml";

const STEP_LABELS: Record<Step, string> = {
  1: "Identity",
  2: "Execution",
  3: "Mode",
  4: "Connector",
  5: "Secrets",
  6: "Finalize",
};

// #690: "where do I get an API key?" deep links live on the wizard
// because the agent-runtime descriptor's `credentialDisplayHint` is a
// free-text hint — these URLs are the stable landing pages we know
// operators should go to for each backend. The hint renders alongside
// them. Keyed by the canonical provider id (the wizard uses
// "anthropic" / "openai" / "google" throughout the credential surface).
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

interface PendingSecret {
  // `id` is only used for React list keys while the user edits the
  // form — it is never sent to the server and never persisted.
  id: string;
  name: string;
  mode: "value" | "externalStoreKey";
  value: string;
  externalStoreKey: string;
}

interface FormState {
  name: string;
  displayName: string;
  description: string;
  // Bug #258: provider is a UI hint only — the server contract carries just
  // `model`. We keep the provider around so the model dropdown can filter,
  // and to make a future typed DTO extension painless.
  provider: string;
  model: string;
  color: string;
  // #350: execution tool, hosting mode
  tool: ExecutionTool;
  hosting: HostingMode;
  // #601: unit-level image + runtime defaults inherited by member
  // agents. Empty strings mean "don't declare"; the wizard only PUTs
  // through the execution endpoint when at least one is filled.
  image: string;
  runtime: string;
  mode: Mode | null;
  // Template mode
  templateId: string | null; // "{package}/{name}"
  // YAML mode
  yamlText: string;
  yamlFileName: string | null;
  // Secrets (#122) — optional, applied after unit creation succeeds.
  secrets: PendingSecret[];
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
}

/**
 * #690: map a runtime id to the canonical provider string previous
 * wizard code passed around for credential-status probes and
 * secret-name resolution. Keeps the `credentialProvider` returned to
 * the CredentialSection as one of the three known tokens
 * ("anthropic" | "openai" | "google") while the agent-runtimes list
 * uses `claude` for the Anthropic backend.
 */
function runtimeIdToProviderId(
  runtimeId: string,
): "anthropic" | "openai" | "google" | null {
  switch (runtimeId.toLowerCase()) {
    case "claude":
    case "anthropic":
      return "anthropic";
    case "openai":
      return "openai";
    case "google":
    case "gemini":
    case "googleai":
      return "google";
    default:
      return null;
  }
}

/**
 * #690: resolve the runtime the wizard needs a credential for, given
 * the current tool+provider inputs and the list of installed runtimes.
 * Returns `null` when no runtime is selected (custom tool), when the
 * selected runtime declares `CredentialKind === "None"` (e.g. local
 * Ollama), or when the runtime is not installed on the tenant.
 */
export function deriveRequiredCredentialRuntime(
  tool: ExecutionTool,
  provider: string,
  runtimes: InstalledAgentRuntimeResponse[] | null,
): InstalledAgentRuntimeResponse | null {
  if (!runtimes || runtimes.length === 0) return null;

  const lookup = (id: string) =>
    runtimes.find((r) => r.id.toLowerCase() === id.toLowerCase()) ?? null;

  switch (tool) {
    case "claude-code":
      return lookup("claude");
    case "codex":
      return lookup("openai");
    case "gemini":
      return lookup("google");
    case "dapr-agent": {
      const normalised = provider.trim().toLowerCase();
      const runtimeId =
        normalised === "anthropic" ? "claude" : normalised;
      const runtime = lookup(runtimeId);
      if (!runtime) return null;
      if (runtime.credentialKind === "None") return null;
      return runtime;
    }
    case "custom":
    default:
      return null;
  }
}

const INITIAL_FORM: FormState = {
  name: "",
  displayName: "",
  description: "",
  // #690: provider is seeded lazily once the agent-runtimes list
  // arrives. An empty string until then; the Execution step renders a
  // loading placeholder.
  provider: "",
  model: "",
  color: DEFAULT_COLOR,
  tool: DEFAULT_EXECUTION_TOOL,
  hosting: DEFAULT_HOSTING_MODE,
  image: "",
  runtime: "",
  mode: null,
  templateId: null,
  yamlText: "",
  yamlFileName: null,
  secrets: [],
  connectorSlug: null,
  connectorTypeId: null,
  connectorConfig: null,
  credentialKey: "",
  saveAsTenantDefault: false,
  credentialOverrideOpen: false,
};

/**
 * Wizard progress rail — v2 reskin (SURF-reskin-create-flows, #859).
 * Styled as a sticky chip-row matching the Explorer's tab bar: brand
 * tint on the active step, filled dot on completed steps, muted pill
 * on the remaining ones. Each step advertises its state via
 * `data-step-state` so tests can key off the new markup without
 * snapshotting the exact class string.
 */
function StepIndicator({ current }: { current: Step }) {
  const steps: Step[] = [1, 2, 3, 4, 5, 6];
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
                {STEP_LABELS[n]}
              </span>
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
  const [step, setStep] = useState<Step>(1);
  const [form, setForm] = useState<FormState>(INITIAL_FORM);
  const [stepError, setStepError] = useState<string | null>(null);
  const [submitError, setSubmitError] = useState<string | null>(null);
  const [submitWarnings, setSubmitWarnings] = useState<string[]>([]);
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

  // Template catalog (#119): cached once per session so revisiting the
  // wizard doesn't round-trip. The key comes from `queryKeys.templates`.
  const templatesQuery = useUnitTemplates();
  const templates = templatesQuery.data ?? null;
  const templatesLoading = templatesQuery.isPending;
  const templatesError = templatesQuery.isError
    ? templatesQuery.error instanceof Error
      ? templatesQuery.error.message
      : String(templatesQuery.error)
    : null;

  // Connector catalog (#199): fetched once so the Connector screen can
  // render the picker without waiting on the server for each render.
  const connectorTypesQuery = useConnectorTypes();
  const connectorTypes = connectorTypesQuery.data ?? null;
  const connectorTypesError = connectorTypesQuery.isError
    ? connectorTypesQuery.error instanceof Error
      ? connectorTypesQuery.error.message
      : String(connectorTypesQuery.error)
    : null;

  // #690: agent runtimes installed on the current tenant. Feeds the
  // provider dropdown (dapr-agent path) and the per-runtime
  // credential/model metadata consumed by the execution step.
  const agentRuntimesQuery = useAgentRuntimes();
  const agentRuntimes = useMemo<InstalledAgentRuntimeResponse[]>(
    () => agentRuntimesQuery.data ?? [],
    [agentRuntimesQuery.data],
  );

  // #350: Ollama model discovery — enabled only when dapr-agent + ollama
  // is selected. The Ollama endpoint is still consulted directly because
  // it surfaces richer per-model metadata (pull status) than the
  // agent-runtimes catalog lookup.
  const ollamaEnabled =
    form.tool === "dapr-agent" && form.provider === "ollama";
  const ollamaQuery = useOllamaModels({ enabled: ollamaEnabled });
  const ollamaModels = ollamaQuery.data?.map((m) => m.name) ?? null;
  const ollamaModelsLoading = ollamaEnabled && ollamaQuery.isPending;

  // #690: Model catalog for the active runtime. The wizard selects
  // the runtime based on tool (fixed-provider tools) or the provider
  // dropdown (dapr-agent). Ollama keeps its dedicated hook above.
  const activeRuntimeId = useMemo<string | null>(() => {
    if (form.tool === "custom") return null;
    if (form.tool === "dapr-agent") {
      const normalised = form.provider.trim().toLowerCase();
      if (!normalised || normalised === "ollama") return null;
      return normalised === "anthropic" ? "claude" : normalised;
    }
    return getToolRuntimeId(form.tool);
  }, [form.tool, form.provider]);
  // Only query the runtime's model catalog when that runtime is
  // actually installed on the tenant — otherwise the wizard surfaces
  // the "no configured agent runtimes" banner, and firing the model
  // fetch would return data for a runtime the platform can't dispatch
  // to. T-07 (#949): with wizard-time credential validation gone, this
  // gate is what keeps the Model dropdown — and therefore Next —
  // disabled when the platform has no matching runtime.
  const activeRuntimeInstalled = useMemo<boolean>(() => {
    if (activeRuntimeId === null) return false;
    return agentRuntimes.some(
      (r) => r.id.toLowerCase() === activeRuntimeId.toLowerCase(),
    );
  }, [activeRuntimeId, agentRuntimes]);
  const agentRuntimeModelsQuery = useAgentRuntimeModels(activeRuntimeId ?? "", {
    enabled: activeRuntimeId !== null && activeRuntimeInstalled,
  });
  const providerModels =
    agentRuntimeModelsQuery.data?.map((m) => m.id) ?? null;

  // #690: seed the provider dropdown from the first installed
  // dapr-agent runtime the first time the runtimes list arrives.
  // The wizard stayed empty before this because the initial form
  // declares `provider: ""` — without the seed the dropdown would
  // render with no selection.
  const daprAgentRuntimes = useMemo(
    () =>
      agentRuntimes.filter(
        (r) => r.toolKind === "dapr-agent",
      ),
    [agentRuntimes],
  );
  // Seed the provider + model fields when the runtimes list arrives
  // and whenever tool/provider changes move the current selection
  // outside the live catalog. Using `useEffect` would trigger
  // cascading renders (react-hooks/set-state-in-effect); the
  // effective values are derived below and the setForm calls are
  // gated through memoised identifiers so they only fire when the
  // stored value actually needs to change.
  const effectiveProvider = useMemo(() => {
    if (form.tool !== "dapr-agent") return form.provider;
    if (form.provider !== "") return form.provider;
    return daprAgentRuntimes.length > 0 ? daprAgentRuntimes[0].id : "";
  }, [form.tool, form.provider, daprAgentRuntimes]);
  if (form.tool === "dapr-agent" && effectiveProvider !== form.provider) {
    setForm((prev) =>
      prev.provider === "" && prev.tool === "dapr-agent"
        ? { ...prev, provider: effectiveProvider }
        : prev,
    );
  }

  const effectiveModel = useMemo(() => {
    if (form.tool === "custom") return form.model;
    if (form.tool === "dapr-agent" && form.provider === "ollama") return form.model;
    if (!providerModels || providerModels.length === 0) return form.model;
    if (providerModels.includes(form.model)) return form.model;
    return providerModels[0];
  }, [form.tool, form.provider, form.model, providerModels]);
  if (effectiveModel !== form.model) {
    setForm((prev) =>
      prev.model === effectiveModel ? prev : { ...prev, model: effectiveModel },
    );
  }

  // #690: derive the runtime that actually needs a credential for the
  // current tool+provider selection. `null` means "no credential
  // required" (custom tool, uninstalled runtime, or `CredentialKind.None`).
  const requiredCredentialRuntime = useMemo(
    () =>
      deriveRequiredCredentialRuntime(
        form.tool,
        form.provider,
        agentRuntimes.length > 0 ? agentRuntimes : null,
      ),
    [form.tool, form.provider, agentRuntimes],
  );
  const requiredCredentialProvider = requiredCredentialRuntime
    ? runtimeIdToProviderId(requiredCredentialRuntime.id)
    : null;

  // Status probe runs whenever a provider needs a key. For the
  // dapr-agent+ollama case it still runs so the existing reachability
  // banner stays visible; when the derivation returns null (custom
  // tool) the query is disabled entirely.
  const credentialProbeProvider =
    requiredCredentialProvider ??
    // When tool=dapr-agent + provider=ollama we still want the banner
    // because the operator expects a reachability read-out. Any other
    // `null` (custom, tool mismatch) skips the probe.
    (form.tool === "dapr-agent" && form.provider === "ollama"
      ? "ollama"
      : null);
  const credentialStatusQuery = useProviderCredentialStatus(
    credentialProbeProvider ?? "",
    { enabled: credentialProbeProvider !== null },
  );
  const credentialStatus = credentialStatusQuery.data ?? null;

  // T-07 (#949): host-side credential validation in the wizard is gone
  // — `POST /api/v1/units` returns 201 immediately and the backend
  // workflow drives validation. The wizard only persists the key; the
  // detail page's Validation panel reports whether the backend accepted
  // it. The Model dropdown always renders against the agent-runtime
  // catalog so Next is never gated on a live reach-out to the LLM.
  const isOllamaDapr =
    form.tool === "dapr-agent" && form.provider === "ollama";
  const activeModelList: readonly string[] | null = useMemo(() => {
    if (isOllamaDapr) return ollamaModels;
    if (providerModels && providerModels.length > 0) return providerModels;
    return null;
  }, [isOllamaDapr, ollamaModels, providerModels]);
  const showModelDropdown = isOllamaDapr || activeModelList !== null;

  // Issue #661: Next on the Execution screen is disabled until a model
  // is selected from the catalog. `form.tool === "custom"` is handled
  // as a separate escape hatch by `canGoNext` / `validateStep2`; they
  // bypass this check entirely.
  const modelIsSelected =
    activeModelList !== null &&
    form.model.trim().length > 0 &&
    activeModelList.includes(form.model);

  const update = <K extends keyof FormState>(key: K, value: FormState[K]) => {
    setForm((prev) => ({ ...prev, [key]: value }));
  };

  const validateStep1 = (): string | null => {
    if (form.mode !== "yaml" && form.mode !== "template") {
      // Scratch / pre-mode-selection — name is required and URL-safe.
      if (!form.name.trim()) return "Name is required.";
      if (!NAME_PATTERN.test(form.name))
        return "Name must be URL-safe (lowercase letters, digits, and hyphens).";
    }
    return null;
  };

  const validateStep2 = (): string | null => {
    // Issue #661: the Execution screen requires a selected model whenever
    // the tool has a known catalog. `modelIsSelected` covers the happy
    // path; the one branch it doesn't cover is "tool=custom" (no catalog
    // at all — skip the check) and "dapr-agent + ollama still loading"
    // (the list is empty, so the user cannot pick anything yet).
    if (form.tool === "custom") return null;
    if (isOllamaDapr && ollamaModelsLoading) {
      return "Wait for the Ollama model list to load before continuing.";
    }
    if (!modelIsSelected) {
      return "Select a model to continue.";
    }
    return null;
  };

  const validateStep3 = (): string | null => {
    if (form.mode === null) return "Select a mode to continue.";
    if (form.mode === "template" && !form.templateId)
      return "Pick a template to continue.";
    if (form.mode === "yaml" && !form.yamlText.trim())
      return "Paste or upload a unit manifest to continue.";
    return null;
  };

  const validateStep4 = (): string | null => {
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
    if (step < 6) setStep((s) => (s + 1) as Step);
  };

  const handleBack = () => {
    setStepError(null);
    setSubmitError(null);
    if (step > 1) setStep((s) => (s - 1) as Step);
  };

  const handleYamlFile = async (file: File) => {
    const text = await file.text();
    setForm((prev) => ({ ...prev, yamlText: text, yamlFileName: file.name }));
  };

  const applyPendingSecrets = async (unitName: string): Promise<string[]> => {
    // Apply the queued secrets after the unit exists. Each failure is
    // collected as a warning so a single bad secret doesn't block the
    // rest — the user gets a clear list back and can retry from the
    // unit's Secrets tab.
    const warnings: string[] = [];
    for (const s of form.secrets) {
      const name = s.name.trim();
      if (!name) continue;
      try {
        await api.createUnitSecret(unitName, {
          name,
          value: s.mode === "value" ? s.value : undefined,
          externalStoreKey:
            s.mode === "externalStoreKey" ? s.externalStoreKey.trim() : undefined,
        });
      } catch (err) {
        const message = err instanceof Error ? err.message : String(err);
        warnings.push(`Secret '${name}': ${message}`);
      }
    }
    return warnings;
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

  const createUnit = useMutation({
    mutationFn: async (): Promise<{
      createdName: string | null;
      warnings: string[];
    }> => {
      const warnings: string[] = [];
      const connector = buildConnectorBinding();
      let createdName: string | null = null;

      // Route through the correct endpoint based on the chosen mode. All three
      // paths ultimately go through the server-side unit-creation service, so
      // the actor-create + directory-register logic is identical. When a
      // connector binding is present it goes on the same request so the
      // server can create + bind atomically (#199).
      // #350: pass execution config if non-default.
      const toolField =
        form.tool !== DEFAULT_EXECUTION_TOOL ? form.tool : undefined;
      const providerField = form.provider || undefined;
      const hostingField =
        form.hosting !== DEFAULT_HOSTING_MODE ? form.hosting : undefined;

      // #626: if the operator supplied an API key AND chose "save as
      // tenant default", write the tenant secret BEFORE the unit is
      // created. A tenant-scope write can fail (permissions, backing
      // store) — we want to know about it before we persist an actor.
      // When the toggle is off the write happens after the unit exists
      // (further down), because the unit id is part of the secret path.
      const runtimeForKey = requiredCredentialRuntime;
      const trimmedKey = form.credentialKey.trim();
      const secretNameForRuntime = runtimeForKey
        ? getRuntimeSecretName(runtimeForKey.id)
        : null;
      const tenantWritePlanned =
        runtimeForKey !== null &&
        secretNameForRuntime !== null &&
        trimmedKey.length > 0 &&
        form.saveAsTenantDefault;
      if (tenantWritePlanned && secretNameForRuntime) {
        const secretName = secretNameForRuntime;
        const tenantSecretExists =
          credentialStatus?.resolvable === true &&
          credentialStatus.source === "tenant";
        try {
          if (tenantSecretExists) {
            // Override flow (§3 of #626): the tenant default already
            // holds a value — rotate the slot to the new key. This is
            // the only "update a tenant default from the wizard" path.
            await api.rotateTenantSecret(secretName, {
              name: secretName,
              value: trimmedKey,
            });
          } else {
            await api.createTenantSecret({
              name: secretName,
              value: trimmedKey,
            });
          }
        } catch (err) {
          const message = err instanceof Error ? err.message : String(err);
          throw new Error(
            `Saving tenant default '${secretName}' failed: ${message}`,
          );
        }
      }

      if (form.mode === "yaml") {
        const resp = await api.createUnitFromYaml({
          yaml: form.yamlText,
          displayName: form.displayName.trim() || undefined,
          color: form.color.trim() || undefined,
          model: form.model.trim() || undefined,
          connector: connector ?? undefined,
          tool: toolField,
          provider: providerField,
          hosting: hostingField,
        } as Parameters<typeof api.createUnitFromYaml>[0]);
        warnings.push(...(resp.warnings ?? []));
        createdName = resp.unit.name;
      } else if (form.mode === "template") {
        const template = templates?.find(
          (t) => `${t.package}/${t.name}` === form.templateId,
        );
        if (!template) {
          throw new Error("Selected template is no longer available.");
        }
        // #325: when the user has filled in a name on Screen 1, pass it
        // through as `unitName` so the created unit uses the caller-
        // supplied address path instead of the manifest's fixed `name`.
        // Without this, two invocations of the same template would
        // collide on the server's unique-name constraint.
        const unitNameOverride = form.name.trim() || undefined;
        const resp = await api.createUnitFromTemplate({
          package: template.package,
          name: template.name,
          unitName: unitNameOverride,
          displayName: form.displayName.trim() || undefined,
          color: form.color.trim() || undefined,
          model: form.model.trim() || undefined,
          connector: connector ?? undefined,
          tool: toolField,
          provider: providerField,
          hosting: hostingField,
        } as Parameters<typeof api.createUnitFromTemplate>[0]);
        warnings.push(...(resp.warnings ?? []));
        createdName = resp.unit.name;
      } else {
        // Scratch — legacy path.
        const created = await api.createUnit({
          name: form.name.trim(),
          displayName: form.displayName.trim() || form.name.trim(),
          description: form.description.trim(),
          model: form.model.trim() || undefined,
          color: form.color.trim() || undefined,
          connector: connector ?? undefined,
          tool: toolField,
          provider: providerField,
          hosting: hostingField,
        });
        createdName = created.name;
      }

      if (createdName && form.secrets.length > 0) {
        const secretWarnings = await applyPendingSecrets(createdName);
        warnings.push(...secretWarnings);
      }

      // #601 B-wide: persist the unit-level execution defaults
      // (image / runtime) through the dedicated
      // /api/v1/units/{id}/execution endpoint after creation. We only
      // PUT when at least one field is filled so units that don't
      // customise the launcher look identical on the wire to pre-#601
      // units. A single failure here is collected as a warning rather
      // than rolling the unit back — the operator can retry from the
      // unit's Execution tab.
      if (createdName) {
        const image = form.image.trim();
        const runtime = form.runtime.trim();
        if (image || runtime) {
          try {
            await api.setUnitExecution(createdName, {
              image: image || null,
              runtime: runtime || null,
            });
          } catch (err) {
            const message = err instanceof Error ? err.message : String(err);
            warnings.push(
              `Execution defaults (image / runtime): ${message}. Retry from the unit's Execution tab.`,
            );
          }
        }
      }

      // #626: when the operator supplied a key with "save as tenant
      // default" off, write the unit-scoped override after the unit
      // exists (the secret path requires the unit id). Failure is
      // surfaced as a warning so the caller can retry from the Secrets
      // tab rather than losing the freshly-created unit.
      if (
        createdName &&
        runtimeForKey !== null &&
        secretNameForRuntime !== null &&
        trimmedKey.length > 0 &&
        !form.saveAsTenantDefault
      ) {
        const secretName = secretNameForRuntime;
        try {
          await api.createUnitSecret(createdName, {
            name: secretName,
            value: trimmedKey,
          });
        } catch (err) {
          const message = err instanceof Error ? err.message : String(err);
          warnings.push(
            `Unit secret '${secretName}' not written: ${message}`,
          );
        }
      }

      return { createdName, warnings };
    },
    onMutate: () => {
      setSubmitError(null);
      setSubmitWarnings([]);
      setStartError(null);
      setStartRequested(false);
      setCreatedUnitName(null);
    },
    onSuccess: ({ createdName, warnings }) => {
      if (warnings.length > 0) setSubmitWarnings(warnings);
      if (createdName) {
        // Invalidate the lists that render the new unit so the detail
        // page and dashboards pick it up on navigation.
        queryClient.invalidateQueries({ queryKey: queryKeys.units.all });
        queryClient.invalidateQueries({ queryKey: queryKeys.dashboard.all });
        toast({ title: "Unit created", description: createdName });
        // Transition into the Validating view on the Finalize step.
        // The unit is in `Draft` right now; the effect below POSTs to
        // `/start` to kick off UnitValidationWorkflow, then polls until
        // terminal. Redirect happens only on success. See #983 / #980.
        setCreatedUnitName(createdName);
      }
    },
    onError: (err) => {
      const message = err instanceof Error ? err.message : String(err);
      setSubmitError(message);
      toast({
        title: "Failed to create unit",
        description: message,
        variant: "destructive",
      });
    },
  });

  // Post-create validation flow. Once `createdUnitName` is set, we
  // POST /start exactly once and begin polling the unit envelope. The
  // SSE stream inside ValidationPanel also invalidates the detail
  // cache, so the polling interval is a fallback — short enough to
  // feel responsive when SSE is unavailable but not so short that it
  // hammers the API while a long image-pull is in flight.
  const startMutation = useMutation({
    mutationFn: (name: string) => api.startUnit(name),
  });
  useEffect(() => {
    if (createdUnitName && !startRequested) {
      setStartRequested(true);
      startMutation.mutate(createdUnitName, {
        onError: (err) => {
          const message = err instanceof Error ? err.message : String(err);
          setStartError(message);
        },
      });
    }
    // We only want to fire start once per created unit. `startMutation`
    // identity is stable within a render cycle for our purposes; the
    // guard is `startRequested`.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [createdUnitName, startRequested]);

  // Poll the newly-created unit until it reaches a terminal status.
  const createdUnitQuery = useUnit(createdUnitName ?? "", {
    enabled: createdUnitName !== null,
    refetchInterval: 1000,
  });
  const createdUnit = createdUnitQuery.data ?? null;
  const createdUnitExecution = useUnitExecution(createdUnitName ?? "", {
    enabled: createdUnitName !== null,
  });

  // Terminal statuses from the validation workflow's perspective.
  // `Running` = start-after-validate succeeded; `Stopped` = validated,
  // awaiting a subsequent start; `Error` = validation failed. `Draft`
  // and `Validating` are in-flight and keep the panel mounted.
  const createdStatus: UnitStatus | null = createdUnit?.status ?? null;
  const isTerminalSuccess =
    createdStatus === "Running" || createdStatus === "Stopped";
  const isTerminalError = createdStatus === "Error";
  const isValidating = createdUnitName !== null && !isTerminalSuccess && !isTerminalError;

  // On terminal success, redirect to the Explorer's Overview tab
  // (#983). The tab query string matches the rest of the app
  // (UnitCard, InboxCard); `node` is the unit name.
  useEffect(() => {
    if (createdUnitName && isTerminalSuccess) {
      router.push(
        `/units?node=${encodeURIComponent(createdUnitName)}&tab=Overview`,
      );
    }
  }, [createdUnitName, isTerminalSuccess, router]);

  const submitting = createUnit.isPending;

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

  const handleCreate = () => {
    createUnit.mutate();
  };

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
      // Identity step. For YAML/template modes the manifest itself
      // supplies the name, so we don't gate advancement on form.name.
      // Name entry is optional on Screen 1 because Mode is still
      // chosen on Screen 3 — the real validation happens in
      // `validateStep1` once Mode is known.
      return true;
    }
    if (step === 2) {
      // Execution step. T-07 (#949): no wizard-time credential
      // validation — the backend validates asynchronously after create
      // and the detail page's Validation panel reports the result.
      // Issue #661: require a selected model before advancing (for
      // tools with a known catalog). Custom tools skip the check —
      // they have no declared model list.
      if (form.tool === "custom") return true;
      if (isOllamaDapr && ollamaModelsLoading) return false;
      return modelIsSelected;
    }
    if (step === 3) {
      if (form.mode === null) return false;
      if (form.mode === "template") return form.templateId !== null;
      if (form.mode === "yaml") return form.yamlText.trim().length > 0;
      return true;
    }
    if (step === 4) {
      // "Skip" is always allowed. If a connector is selected, require a
      // valid config payload from the connector's wizard step.
      if (form.connectorSlug === null) return true;
      return form.connectorConfig !== null;
    }
    return true;
  }, [step, form, isOllamaDapr, ollamaModelsLoading, modelIsSelected]);

  // Issue #927-followup (post-T-07): explain *why* Next is disabled on
  // Step 2. Without this hint the wizard can dead-end silently — the
  // Model dropdown only renders when the agent-runtimes catalog returns
  // a matching runtime, so an unreachable platform API or an
  // uninstalled runtime collapses the model surface and leaves the
  // operator staring at a disabled button with no way to diagnose. We
  // surface the most specific actionable reason, in priority order,
  // mirroring the gates `canGoNext` / `validateStep2` consult.
  const nextDisabledReason = useMemo<string | null>(() => {
    if (step !== 2) return null;
    if (canGoNext) return null;
    if (form.tool === "custom") return null;
    if (agentRuntimesQuery.isPending) {
      return "Loading the agent-runtime catalog…";
    }
    if (agentRuntimesQuery.isError) {
      return "Could not load the agent-runtime catalog.";
    }
    const toolLabel =
      EXECUTION_TOOLS.find((t) => t.id === form.tool)?.label ?? form.tool;
    if (agentRuntimes.length === 0) {
      return "No configured agent runtimes.";
    }
    if (form.tool === "dapr-agent" && form.provider.trim() === "") {
      return "Pick an LLM provider for the Dapr Agent runtime.";
    }
    if (form.tool !== "dapr-agent" && requiredCredentialRuntime === null) {
      return `The "${toolLabel}" agent runtime is not installed on this server. Pick a different execution tool, or install the matching runtime.`;
    }
    if (isOllamaDapr && ollamaModelsLoading) {
      return "Loading the model list from the Ollama server…";
    }
    if (!showModelDropdown) {
      return "No model catalog is available yet — wait for the catalog to load, or pick a different execution tool.";
    }
    if (!modelIsSelected) {
      return "Select a model from the dropdown to continue.";
    }
    return null;
  }, [
    step,
    canGoNext,
    form.tool,
    form.provider,
    agentRuntimesQuery.isPending,
    agentRuntimesQuery.isError,
    agentRuntimes.length,
    requiredCredentialRuntime,
    isOllamaDapr,
    ollamaModelsLoading,
    showModelDropdown,
    modelIsSelected,
  ]);

  // Truthy when the agent-runtime catalog itself is the cause of an
  // empty Step 2 (no runtimes / fetch failure for a non-custom tool).
  // Drives the in-card banner above the form so the operator sees the
  // root cause, not just the "Next is disabled" symptom underneath.
  // The fetch-error message is intentionally short — the platform API
  // is supposed to always be reachable from the dashboard host (the
  // OSS deployment proxies `/api/v1/*` to it; private cloud serves
  // both off the same origin), so a failure here is an infrastructure
  // problem the operator needs to debug from logs, not from a banner
  // dump of the response body.
  const agentRuntimeCatalogIssue = useMemo<string | null>(() => {
    if (form.tool === "custom") return null;
    if (agentRuntimesQuery.isPending) return null;
    if (agentRuntimesQuery.isError) {
      return "Could not load the agent-runtime catalog.";
    }
    if (agentRuntimes.length === 0) {
      return "No configured agent runtimes.";
    }
    return null;
  }, [
    form.tool,
    agentRuntimesQuery.isPending,
    agentRuntimesQuery.isError,
    agentRuntimes.length,
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
          <h1 className="text-2xl font-bold">Create a unit</h1>
        </div>
        <p className="text-sm text-muted-foreground">
          Register a new unit and wire up its runtime. Mirrors{" "}
          <code className="rounded bg-muted px-1 py-0.5 font-mono text-xs">
            spring unit create
          </code>
          .
        </p>
      </div>

      <StepIndicator current={step} />

      {step === 1 && (
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
                the unit&apos;s address (e.g. <code>my-unit</code>). When
                creating from a template, you can leave it blank to use the
                template&apos;s built-in name, or enter a value to override
                it. When importing a YAML manifest, the name in the
                manifest always takes precedence.
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

            {stepError && (
              <p className="rounded-md border border-destructive/50 bg-destructive/10 px-3 py-2 text-sm text-destructive">
                {stepError}
              </p>
            )}
          </CardContent>
        </Card>
      )}

      {step === 2 && (
        <Card>
          <CardHeader>
            <CardTitle>Execution tool &amp; model</CardTitle>
          </CardHeader>
          <CardContent className="space-y-4">
            {agentRuntimeCatalogIssue && (
              <div
                role="alert"
                data-testid="agent-runtime-catalog-issue"
                className="flex items-start gap-2 rounded-md border border-warning/50 bg-warning/15 px-3 py-2 text-sm text-foreground"
              >
                <AlertTriangle
                  className="mt-0.5 h-4 w-4 shrink-0"
                  aria-hidden
                />
                <p className="flex-1">{agentRuntimeCatalogIssue}</p>
              </div>
            )}

            {/* Issue #661 order: Tool → credential input → Model. */}
            <label className="block space-y-1">
              <span className="text-sm text-muted-foreground">
                Execution tool
              </span>
              <select
                value={form.tool}
                onChange={(e) => {
                  // #690: model selection is seeded by the
                  // `useAgentRuntimeModels` effect once the catalog
                  // arrives, so the tool-change handler just clears
                  // the current model and lets the effect snap to
                  // the new runtime's default.
                  const nextTool = e.target.value as ExecutionTool;
                  setForm((prev) => ({ ...prev, tool: nextTool, model: "" }));
                }}
                aria-label="Execution tool"
                className="flex h-9 w-full rounded-md border border-input bg-background px-3 py-1 text-sm shadow-sm focus-visible:outline-none focus-visible:ring-1 focus-visible:ring-ring disabled:cursor-not-allowed disabled:opacity-50"
              >
                {EXECUTION_TOOLS.map((t) => (
                  <option key={t.id} value={t.id}>
                    {t.label}
                  </option>
                ))}
              </select>
            </label>

            {/*
              #598 + #690: Provider renders only when the execution tool
              is `dapr-agent`. The list is sourced from the installed
              agent runtimes whose tool-kind is `dapr-agent` — Claude
              Code, Codex, and Gemini hard-code their own provider
              inside the tool CLI, so exposing a Provider dropdown for
              them would be misleading.
            */}
            {form.tool === "dapr-agent" && (
              <label className="block space-y-1">
                <span className="text-sm text-muted-foreground">
                  LLM Provider
                </span>
                <select
                  value={form.provider}
                  onChange={(e) => {
                    const nextProvider = e.target.value;
                    setForm((prev) => ({
                      ...prev,
                      provider: nextProvider,
                      model: "",
                    }));
                  }}
                  aria-label="LLM provider"
                  disabled={daprAgentRuntimes.length === 0}
                  className="flex h-9 w-full rounded-md border border-input bg-background px-3 py-1 text-sm shadow-sm focus-visible:outline-none focus-visible:ring-1 focus-visible:ring-ring disabled:cursor-not-allowed disabled:opacity-50"
                >
                  {daprAgentRuntimes.map((r) => (
                    <option key={r.id} value={r.id}>
                      {r.displayName}
                    </option>
                  ))}
                </select>
              </label>
            )}

            {/*
              #626 + #659: credential input with the per-provider
              "Get an API key" deep link attached.
            */}
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
                onToggleSaveAsTenantDefault={(v) =>
                  update("saveAsTenantDefault", v)
                }
                onToggleOverride={(v) => {
                  setForm((prev) => ({
                    ...prev,
                    credentialOverrideOpen: v,
                    // Toggling the override off clears any typed value so
                    // it doesn't silently apply after the operator thinks
                    // they cancelled.
                    credentialKey: v ? prev.credentialKey : "",
                    saveAsTenantDefault: v ? prev.saveAsTenantDefault : false,
                  }));
                }}
              />
            )}

            {/* Ollama reachability banner (stand-alone when no API key
                is required). */}
            {form.tool === "dapr-agent" &&
              form.provider === "ollama" &&
              credentialStatus && (
                <OllamaReachabilityBanner data={credentialStatus} />
              )}

            {/*
              #655 + issue #661: the Model dropdown is revealed only
              when a live model source is available — either the
              tenant/unit credential resolved, the operator just
              validated a key, or the Ollama provider is in use.
              Otherwise we hide the dropdown entirely so operators
              aren't asked to pick from a stale static fallback before
              the wizard knows their account works. Next stays disabled
              until a model is picked.
            */}
            {showModelDropdown && (
              <label className="block space-y-1">
                <span className="text-sm text-muted-foreground">Model</span>
                <select
                  value={form.model}
                  onChange={(e) => update("model", e.target.value)}
                  aria-label="Model"
                  disabled={isOllamaDapr && ollamaModelsLoading}
                  className="flex h-9 w-full rounded-md border border-input bg-background px-3 py-1 text-sm shadow-sm focus-visible:outline-none focus-visible:ring-1 focus-visible:ring-ring disabled:cursor-not-allowed disabled:opacity-50"
                >
                  {(activeModelList ?? []).map((m) => (
                    <option key={m} value={m}>
                      {m}
                    </option>
                  ))}
                </select>
                {isOllamaDapr && ollamaModelsLoading && (
                  <span className="block text-xs text-muted-foreground">
                    Loading models from Ollama server...
                  </span>
                )}
              </label>
            )}

            {/* Issue #661: execution-environment section. Visually
                separated from the tool/model block with a heading +
                border. */}
            <div className="space-y-3 border-t border-border pt-4">
              <div>
                <h3 className="text-sm font-semibold">
                  Execution environment
                </h3>
                <p className="text-xs text-muted-foreground">
                  Defaults inherited by member agents. Leave blank to use
                  platform defaults; individual agents can override each
                  field on their Execution panel.
                </p>
              </div>

              <div className="grid grid-cols-1 gap-4 sm:grid-cols-2">
                <label className="block space-y-1">
                  <span className="text-sm text-muted-foreground">
                    Image (default)
                  </span>
                  <Input
                    value={form.image}
                    onChange={(e) => update("image", e.target.value)}
                    placeholder="ghcr.io/... or spring-agent:latest"
                    aria-label="Execution image"
                  />
                  <span className="block text-xs text-muted-foreground">
                    Default container image used to launch member agents.
                  </span>
                </label>

                <label className="block space-y-1">
                  <span className="text-sm text-muted-foreground">
                    Hosting mode
                  </span>
                  <select
                    value={form.hosting}
                    onChange={(e) =>
                      update("hosting", e.target.value as HostingMode)
                    }
                    aria-label="Hosting mode"
                    className="flex h-9 w-full rounded-md border border-input bg-background px-3 py-1 text-sm shadow-sm focus-visible:outline-none focus-visible:ring-1 focus-visible:ring-ring disabled:cursor-not-allowed disabled:opacity-50"
                  >
                    {HOSTING_MODES.map((m) => (
                      <option key={m.id} value={m.id}>
                        {m.label}
                      </option>
                    ))}
                  </select>
                  <span className="block text-xs text-muted-foreground">
                    How long the agent process lives between work items.
                  </span>
                </label>

                <label className="block space-y-1">
                  <span className="text-sm text-muted-foreground">
                    Runtime (default)
                  </span>
                  <select
                    value={form.runtime}
                    onChange={(e) => update("runtime", e.target.value)}
                    aria-label="Execution runtime"
                    className="flex h-9 w-full rounded-md border border-input bg-background px-3 py-1 text-sm shadow-sm focus-visible:outline-none focus-visible:ring-1 focus-visible:ring-ring disabled:cursor-not-allowed disabled:opacity-50"
                  >
                    <option value="">(leave to default)</option>
                    {EXECUTION_RUNTIMES.map((r) => (
                      <option key={r} value={r}>
                        {r}
                      </option>
                    ))}
                  </select>
                  <span className="block text-xs text-muted-foreground">
                    Container runtime the launcher drives.
                  </span>
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

      {step === 3 && (
        <Card>
          <CardHeader>
            <CardTitle>Choose a mode</CardTitle>
          </CardHeader>
          <CardContent className="space-y-3">
            <ModeCard
              icon={<FileText className="h-5 w-5" />}
              title="Template"
              description="Start from a pre-built team template shipped with a package."
              selected={form.mode === "template"}
              onSelect={() => update("mode", "template")}
            />

            {form.mode === "template" && (
              <div className="ml-11 space-y-2 rounded-md border border-border bg-muted/30 p-3">
                {templatesLoading && (
                  <p className="text-xs text-muted-foreground">
                    Loading templates…
                  </p>
                )}
                {templatesError && (
                  <p className="text-xs text-destructive">
                    Failed to load templates: {templatesError}
                  </p>
                )}
                {!templatesLoading && templates && templates.length === 0 && (
                  <p className="text-xs text-muted-foreground">
                    No templates discovered. Make sure the API is running from a
                    repo checkout that includes <code>packages/</code>.
                  </p>
                )}
                {templates && templates.length > 0 && (
                  <ul className="space-y-1.5">
                    {templates.map((t) => {
                      const id = `${t.package}/${t.name}`;
                      const isSelected = form.templateId === id;
                      return (
                        <li key={id}>
                          <button
                            type="button"
                            onClick={() => update("templateId", id)}
                            className={cn(
                              "flex w-full items-start gap-2 rounded-md border p-2 text-left text-sm transition-colors",
                              isSelected
                                ? "border-primary bg-primary/5"
                                : "border-border hover:bg-accent/50",
                            )}
                          >
                            <span className="mt-0.5 h-4 w-4 shrink-0 rounded-full border border-border bg-background">
                              {isSelected && (
                                <span className="block h-full w-full rounded-full bg-primary" />
                              )}
                            </span>
                            <span className="flex-1">
                              <span className="font-medium">
                                {t.package}/{t.name}
                              </span>
                              {t.description && (
                                <span className="block text-xs text-muted-foreground">
                                  {t.description}
                                </span>
                              )}
                              <span className="block text-[11px] text-muted-foreground">
                                {t.path}
                              </span>
                            </span>
                          </button>
                        </li>
                      );
                    })}
                  </ul>
                )}
              </div>
            )}

            <ModeCard
              icon={<Sparkles className="h-5 w-5" />}
              title="Scratch"
              description="Create an empty unit you can configure manually."
              selected={form.mode === "scratch"}
              onSelect={() => update("mode", "scratch")}
            />

            <ModeCard
              icon={<FileCode className="h-5 w-5" />}
              title="YAML"
              description="Import an existing unit manifest (same grammar as the CLI's spring apply)."
              selected={form.mode === "yaml"}
              onSelect={() => update("mode", "yaml")}
            />

            {form.mode === "yaml" && (
              <div className="ml-11 space-y-2 rounded-md border border-border bg-muted/30 p-3">
                <label className="block space-y-1 text-xs text-muted-foreground">
                  <span>Upload a .yaml file</span>
                  <input
                    type="file"
                    accept=".yaml,.yml,text/yaml"
                    onChange={async (e) => {
                      const file = e.target.files?.[0];
                      if (file) await handleYamlFile(file);
                    }}
                    className="block text-sm"
                  />
                </label>
                <label className="block space-y-1 text-xs text-muted-foreground">
                  <span>
                    Manifest contents
                    {form.yamlFileName && (
                      <span className="ml-2 text-[11px] text-muted-foreground">
                        ({form.yamlFileName})
                      </span>
                    )}
                  </span>
                  <textarea
                    value={form.yamlText}
                    onChange={(e) => update("yamlText", e.target.value)}
                    placeholder={"unit:\n  name: engineering-team\n  description: ..."}
                    rows={10}
                    className="w-full rounded-md border border-input bg-background px-3 py-2 font-mono text-xs"
                    spellCheck={false}
                  />
                </label>
              </div>
            )}

            {stepError && (
              <p className="rounded-md border border-destructive/50 bg-destructive/10 px-3 py-2 text-sm text-destructive">
                {stepError}
              </p>
            )}
          </CardContent>
        </Card>
      )}

      {step === 4 && (
        <Card>
          <CardHeader>
            <CardTitle className="flex items-center gap-2">
              <Plug className="h-5 w-5" /> Connector
            </CardTitle>
          </CardHeader>
          <CardContent className="space-y-4 text-sm">
            <p className="text-muted-foreground">
              Optionally bind this unit to a connector during creation. The
              binding is applied atomically with the unit — if it fails, the
              unit is rolled back and nothing is persisted. Leave on{" "}
              <strong>Skip</strong> to configure a connector later from the
              unit&apos;s Connector tab.
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
                    Create the unit without a connector binding. You can add
                    one later.
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
                      isSelected
                        ? "border-primary bg-primary/5"
                        : "border-border",
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
                            // Clearing here forces the connector's step
                            // component to rebuild its initial state.
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
                        initialValue={null}
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

      {step === 5 && (
        <Card>
          <CardHeader>
            <CardTitle className="flex items-center gap-2">
              <KeyRound className="h-5 w-5" /> Secrets
            </CardTitle>
          </CardHeader>
          <CardContent className="space-y-3 text-sm">
            <p className="text-muted-foreground">
              Optionally register one or more unit-scoped secrets. Each entry
              is applied after the unit is created — a single failing secret
              surfaces as a warning on the final step and can be retried from
              the unit&apos;s Secrets tab.
            </p>

            {requiredCredentialProvider !== null &&
              requiredCredentialRuntime !== null &&
              credentialStatus?.resolvable === true &&
              credentialStatus?.source === "tenant" && (
                <TenantDefaultSecretRow
                  provider={requiredCredentialProvider}
                  secretName={
                    getRuntimeSecretName(requiredCredentialRuntime.id) ?? ""
                  }
                  overrideOpen={form.credentialOverrideOpen}
                  credentialKey={form.credentialKey}
                  saveAsTenantDefault={form.saveAsTenantDefault}
                  onToggleOverride={(v) => {
                    setForm((prev) => ({
                      ...prev,
                      credentialOverrideOpen: v,
                      credentialKey: v ? prev.credentialKey : "",
                      saveAsTenantDefault: v
                        ? prev.saveAsTenantDefault
                        : false,
                    }));
                  }}
                  onKeyChange={(v) => update("credentialKey", v)}
                  onToggleSaveAsTenantDefault={(v) =>
                    update("saveAsTenantDefault", v)
                  }
                />
              )}

            {form.secrets.length === 0 && (
              <p className="text-muted-foreground">No secrets queued.</p>
            )}

            {form.secrets.map((s, idx) => (
              <div
                key={s.id}
                className="space-y-2 rounded-md border border-border p-3"
              >
                <div className="flex items-center gap-2">
                  <Input
                    value={s.name}
                    onChange={(e) =>
                      setForm((prev) => ({
                        ...prev,
                        secrets: prev.secrets.map((it, i) =>
                          i === idx ? { ...it, name: e.target.value } : it,
                        ),
                      }))
                    }
                    placeholder="secret name"
                    autoComplete="off"
                  />
                  <Button
                    size="sm"
                    variant="outline"
                    onClick={() =>
                      setForm((prev) => ({
                        ...prev,
                        secrets: prev.secrets.filter((_, i) => i !== idx),
                      }))
                    }
                    aria-label="Remove secret"
                  >
                    Remove
                  </Button>
                </div>
                <div className="flex gap-2">
                  <Button
                    size="sm"
                    variant={s.mode === "value" ? "default" : "outline"}
                    onClick={() =>
                      setForm((prev) => ({
                        ...prev,
                        secrets: prev.secrets.map((it, i) =>
                          i === idx ? { ...it, mode: "value" } : it,
                        ),
                      }))
                    }
                  >
                    Pass-through value
                  </Button>
                  <Button
                    size="sm"
                    variant={
                      s.mode === "externalStoreKey" ? "default" : "outline"
                    }
                    onClick={() =>
                      setForm((prev) => ({
                        ...prev,
                        secrets: prev.secrets.map((it, i) =>
                          i === idx
                            ? { ...it, mode: "externalStoreKey" }
                            : it,
                        ),
                      }))
                    }
                  >
                    External reference
                  </Button>
                </div>
                {s.mode === "value" ? (
                  <Input
                    type="password"
                    value={s.value}
                    onChange={(e) =>
                      setForm((prev) => ({
                        ...prev,
                        secrets: prev.secrets.map((it, i) =>
                          i === idx ? { ...it, value: e.target.value } : it,
                        ),
                      }))
                    }
                    placeholder="Value (stored server-side; never returned)"
                    autoComplete="off"
                    spellCheck={false}
                  />
                ) : (
                  <Input
                    value={s.externalStoreKey}
                    onChange={(e) =>
                      setForm((prev) => ({
                        ...prev,
                        secrets: prev.secrets.map((it, i) =>
                          i === idx
                            ? { ...it, externalStoreKey: e.target.value }
                            : it,
                        ),
                      }))
                    }
                    placeholder="kv://vault/secret-id"
                    autoComplete="off"
                  />
                )}
              </div>
            ))}

            <Button
              variant="outline"
              onClick={() =>
                setForm((prev) => ({
                  ...prev,
                  secrets: [
                    ...prev.secrets,
                    {
                      id: `s-${Date.now()}-${prev.secrets.length}`,
                      name: "",
                      mode: "value",
                      value: "",
                      externalStoreKey: "",
                    },
                  ],
                }))
              }
            >
              Add secret
            </Button>
          </CardContent>
        </Card>
      )}

      {step === 6 && (
        <Card>
          <CardHeader>
            <CardTitle className="flex items-center gap-2">
              <Rocket className="h-5 w-5" /> Finalize
            </CardTitle>
          </CardHeader>
          <CardContent className="space-y-4 text-sm">
            <div className="rounded-md border border-border p-3 space-y-1">
              <SummaryRow label="Name" value={renderNameSummary(form)} />
              <SummaryRow
                label="Display name"
                value={form.displayName || "—"}
              />
              {form.mode === "scratch" && (
                <SummaryRow
                  label="Description"
                  value={form.description || "—"}
                />
              )}
              <SummaryRow label="Model" value={form.model || "(not selected)"} />
              <SummaryRow label="Color" value={form.color || DEFAULT_COLOR} />
              <SummaryRow label="Image" value={form.image || "(leave to default)"} />
              <SummaryRow label="Runtime" value={form.runtime || "(leave to default)"} />
              <SummaryRow
                label="Mode"
                value={form.mode ? form.mode : "—"}
              />
              {form.mode === "template" && form.templateId && (
                <SummaryRow label="Template" value={form.templateId} />
              )}
              {form.mode === "yaml" && (
                <SummaryRow
                  label="YAML"
                  value={
                    form.yamlFileName
                      ? form.yamlFileName
                      : `${form.yamlText.split("\n").length} lines pasted`
                  }
                />
              )}
              <SummaryRow
                label="Connector"
                value={
                  form.connectorSlug === null
                    ? "(skipped)"
                    : form.connectorConfig === null
                      ? `${form.connectorSlug} (incomplete — will not bind)`
                      : form.connectorSlug
                }
              />
            </div>

            {submitError && (
              <p className="rounded-md border border-destructive/50 bg-destructive/10 px-3 py-2 text-sm text-destructive">
                {submitError}
              </p>
            )}

            {submitWarnings.length > 0 && (
              <div className="rounded-md border border-amber-500/50 bg-amber-500/10 px-3 py-2 text-sm text-amber-900 dark:text-amber-200">
                <p className="font-medium">
                  Unit created with {submitWarnings.length} warning
                  {submitWarnings.length === 1 ? "" : "s"}:
                </p>
                <ul className="mt-1 list-disc pl-5">
                  {submitWarnings.map((w, i) => (
                    <li key={i}>{w}</li>
                  ))}
                </ul>
              </div>
            )}

            {missingCredential && missingCredentialMessage && !createdUnitName && (
              <p
                role="alert"
                data-testid="missing-credential-message"
                className="rounded-md border border-warning/50 bg-warning/15 px-3 py-2 text-sm text-foreground"
              >
                {missingCredentialMessage}
              </p>
            )}

            {startError && (
              <p
                role="alert"
                data-testid="wizard-start-error"
                className="rounded-md border border-destructive/50 bg-destructive/10 px-3 py-2 text-sm text-destructive"
              >
                Failed to start validation: {startError}
              </p>
            )}

            {createdUnitName && !createdUnit && !createdUnitQuery.error && (
              <p
                role="status"
                data-testid="wizard-validation-loading"
                className="rounded-md border border-border bg-muted/30 px-3 py-2 text-sm text-muted-foreground"
              >
                Starting validation for {createdUnitName}…
              </p>
            )}

            {createdUnit && (
              <div data-testid="wizard-validation-view">
                <ValidationPanel
                  unit={createdUnit}
                  image={createdUnitExecution.data?.image ?? null}
                  runtime={createdUnitExecution.data?.runtime ?? null}
                />
              </div>
            )}

            {!createdUnitName && (
              <Button
                onClick={handleCreate}
                disabled={submitting || missingCredential}
                data-testid="create-unit-button"
              >
                {submitting ? "Creating…" : "Create unit"}
              </Button>
            )}

            {isValidating && (
              <p
                role="status"
                data-testid="wizard-validation-status"
                className="text-xs text-muted-foreground"
              >
                Waiting for validation to finish. You&apos;ll be redirected to
                the Explorer once the unit is ready.
              </p>
            )}

            {isTerminalError && (
              <div
                data-testid="wizard-validation-error-actions"
                className="flex flex-wrap items-center gap-2 rounded-md border border-destructive/30 bg-destructive/5 px-3 py-2"
              >
                <p className="flex-1 text-sm text-foreground">
                  Validation failed. Step back to adjust your inputs (for
                  example, paste a new credential or pick a different tool) and
                  try again.
                </p>
                <Button
                  variant="outline"
                  size="sm"
                  data-testid="wizard-validation-back"
                  onClick={() => {
                    // Unwind the create-phase state so the wizard returns to a
                    // pristine editable state. We don't reset the form itself —
                    // the operator's choices are preserved so they can fix the
                    // offending field without re-entering everything.
                    setCreatedUnitName(null);
                    setStartRequested(false);
                    setStartError(null);
                    setSubmitError(null);
                    setSubmitWarnings([]);
                    setStep(2);
                  }}
                >
                  <ArrowLeft className="mr-1.5 h-3.5 w-3.5" aria-hidden />
                  Back to Execution
                </Button>
              </div>
            )}
          </CardContent>
        </Card>
      )}

      <div className="flex flex-col gap-2">
        <div className="flex items-center justify-between gap-3">
          <Button
            variant="outline"
            onClick={handleBack}
            // Disable Back while the initial create mutation is in flight
            // or while we're actively waiting for validation to finish —
            // the unit exists at that point and the wizard form controls
            // are decoupled from the backend state. Terminal-error state
            // re-enables Back so the operator can fix a field and retry.
            disabled={step === 1 || submitting || isValidating}
          >
            Back
          </Button>
          {step < 6 && (
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

function renderNameSummary(form: FormState): string {
  if (form.mode === "template" && form.templateId) {
    return `(from template ${form.templateId})`;
  }
  if (form.mode === "yaml") {
    return "(from YAML manifest)";
  }
  return form.name || "—";
}

function ModeCard({
  icon,
  title,
  description,
  selected,
  onSelect,
}: {
  icon: React.ReactNode;
  title: string;
  description: string;
  selected: boolean;
  onSelect: () => void;
}) {
  return (
    <button
      type="button"
      onClick={onSelect}
      aria-pressed={selected}
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
 *   - Ollama (dapr-agent + provider=ollama) → reuses PR #627's
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
interface CredentialSectionProps {
  requiredProvider: "anthropic" | "openai" | "google" | null;
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
 * it verbatim when the tool is `dapr-agent + provider=ollama`.
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
