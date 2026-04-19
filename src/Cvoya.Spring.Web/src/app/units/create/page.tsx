"use client";

import { useCallback, useMemo, useState } from "react";
import { useRouter } from "next/navigation";
import { useMutation, useQueryClient } from "@tanstack/react-query";
import {
  AlertTriangle,
  Check,
  CheckCircle2,
  Eye,
  EyeOff,
  FileCode,
  FileText,
  KeyRound,
  Plug,
  Rocket,
  Sparkles,
} from "lucide-react";

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
  useConnectorTypes,
  useOllamaModels,
  useProviderCredentialStatus,
  useProviderModels,
  useUnitTemplates,
} from "@/lib/api/queries";
import { queryKeys } from "@/lib/api/query-keys";
import type { UnitConnectorBindingRequest } from "@/lib/api/types";
import { EXECUTION_RUNTIMES } from "@/lib/api/types";
import {
  AI_PROVIDERS,
  DEFAULT_EXECUTION_TOOL,
  DEFAULT_HOSTING_MODE,
  DEFAULT_MODEL,
  DEFAULT_PROVIDER_ID,
  EXECUTION_TOOLS,
  HOSTING_MODES,
  getProvider,
  getToolModelProvider,
  type ExecutionTool,
  type HostingMode,
} from "@/lib/ai-models";
import { cn } from "@/lib/utils";

const DEFAULT_COLOR = "#6366f1";

const NAME_PATTERN = /^[a-z0-9-]+$/;

// Secrets step (#122) is implemented inline; connector binding (#199) is
// implemented via a registry-provided per-connector React component that
// produces a payload we bundle into the single create-unit call.

type Step = 1 | 2 | 3 | 4 | 5;
type Mode = "template" | "scratch" | "yaml";

const STEP_LABELS: Record<Step, string> = {
  1: "Details",
  2: "Mode",
  3: "Connector",
  4: "Secrets",
  5: "Finalize",
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
  // tool+provider at render time (see `deriveRequiredCredentialProvider`).
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
 * #626: the canonical secret name used by the `ILlmCredentialResolver`
 * for each tier-2 provider. The server reads these same names when
 * resolving credentials at dispatch time. Kept in lock-step with
 * `src/Cvoya.Spring.Dapr/Execution/LlmCredentialResolver.cs` — the
 * mapping is small, fixed, and has no reason to live on the wire.
 */
const PROVIDER_SECRET_NAMES: Readonly<Record<string, string>> = {
  anthropic: "anthropic-api-key",
  openai: "openai-api-key",
  google: "google-api-key",
};

/**
 * #626: derive the provider that actually needs an LLM credential,
 * given the currently-selected tool and (for dapr-agent) provider
 * dropdown. The mapping is intentionally terse:
 *
 *   - claude-code → anthropic   (Claude Code talks to Anthropic)
 *   - codex       → openai
 *   - gemini      → google
 *   - dapr-agent  → the selected provider, mapped to its canonical id
 *     (anthropic / openai / google); "ollama" returns null (no secret
 *     — Ollama is tier-1 reachability) and an unrecognised provider
 *     returns null as a safe default.
 *   - custom      → null (contract undefined; the custom launcher
 *     declares its own secrets if any)
 *
 * Returns `null` when no API-key is required for the selection. That
 * is the signal the wizard uses to hide the inline credential surface
 * altogether (Ollama/custom paths).
 */
export function deriveRequiredCredentialProvider(
  tool: ExecutionTool,
  provider: string,
): "anthropic" | "openai" | "google" | null {
  switch (tool) {
    case "claude-code":
      return "anthropic";
    case "codex":
      return "openai";
    case "gemini":
      return "google";
    case "dapr-agent":
      switch (provider) {
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
          // ollama (tier-1, reachability only) + unknowns fall through.
          return null;
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
  provider: DEFAULT_PROVIDER_ID,
  model: DEFAULT_MODEL,
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

function StepIndicator({ current }: { current: Step }) {
  const steps: Step[] = [1, 2, 3, 4, 5];
  return (
    <div className="sticky top-0 z-10 -mx-4 md:-mx-6 bg-background/80 backdrop-blur border-b border-border px-4 md:px-6 py-3">
      <ol className="flex items-center gap-2 overflow-x-auto">
        {steps.map((n, idx) => {
          const done = n < current;
          const active = n === current;
          return (
            <li key={n} className="flex items-center gap-2 whitespace-nowrap">
              <span
                className={cn(
                  "flex h-6 w-6 items-center justify-center rounded-full text-xs font-semibold",
                  done && "bg-primary text-primary-foreground",
                  active && "bg-primary/20 text-primary ring-2 ring-primary",
                  !done && !active && "bg-muted text-muted-foreground",
                )}
              >
                {done ? <Check className="h-3.5 w-3.5" /> : n}
              </span>
              <span
                className={cn(
                  "text-sm",
                  active ? "font-medium text-foreground" : "text-muted-foreground",
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
    </div>
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

  // Connector catalog (#199): fetched once so Step 3 can render the
  // picker without waiting on the server for each render.
  const connectorTypesQuery = useConnectorTypes();
  const connectorTypes = connectorTypesQuery.data ?? null;
  const connectorTypesError = connectorTypesQuery.isError
    ? connectorTypesQuery.error instanceof Error
      ? connectorTypesQuery.error.message
      : String(connectorTypesQuery.error)
    : null;

  // #350: Ollama model discovery — enabled only when dapr-agent + ollama
  // is selected. When disabled the query is idle, mirroring the previous
  // `useEffect` early-return behaviour.
  const ollamaEnabled =
    form.tool === "dapr-agent" && form.provider === "ollama";
  const ollamaQuery = useOllamaModels({ enabled: ollamaEnabled });
  const ollamaModels = ollamaQuery.data?.map((m) => m.name) ?? null;
  const ollamaModelsLoading = ollamaEnabled && ollamaQuery.isPending;

  // #597: provider-agnostic dynamic model list. The server probes the
  // provider's own models endpoint (Anthropic, OpenAI) when a key is
  // configured, so the dropdown tracks the current catalog without a
  // code change. The server falls back to its static list on any error,
  // so a null here only means the fetch itself collapsed (e.g. auth
  // denied on the portal side); `ai-models.ts` is the client-side safety
  // net in that case. Skipped for ollama — the #350 path is richer
  // (includes pull status) and we don't want two overlapping requests.
  //
  // #641: when the tool hides the Provider dropdown (claude-code / codex
  // / gemini), we still need a Model dropdown populated from that tool's
  // catalog — derive the provider id from the tool and reuse the same
  // hook. For `dapr-agent`, fall back to `form.provider`.
  const toolModelProvider = getToolModelProvider(form.tool);
  const effectiveProviderForModels =
    form.tool === "dapr-agent" ? form.provider : (toolModelProvider ?? "");
  const providerModelsEnabled =
    effectiveProviderForModels !== "" && effectiveProviderForModels !== "ollama";
  const providerModelsQuery = useProviderModels(effectiveProviderForModels, {
    enabled: providerModelsEnabled,
  });
  const providerModels = providerModelsQuery.data ?? null;

  // #626: derive the provider that actually needs an API key, given
  // the currently-selected tool+provider. For Claude Code / Codex /
  // Gemini the provider is fixed regardless of what the Provider
  // dropdown shows (the dropdown only renders for dapr-agent anyway);
  // for dapr-agent we thread the current dropdown value through. A
  // `null` return means "no key required for this selection" (custom
  // tool, ollama) and hides the credential surface entirely.
  const requiredCredentialProvider = deriveRequiredCredentialProvider(
    form.tool,
    form.provider,
  );

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

  // #655: wizard-time credential validation. The operator types a key
  // into the CredentialSection; clicking Validate POSTs it to the
  // provider via the server-side validator. On success the returned
  // model ids seed the Model dropdown so the operator picks from
  // whatever their account actually supports, not a stale static list.
  // `validatedKey` + `validatedProvider` pin the verdict to the exact
  // input that produced it — any subsequent edit invalidates the
  // verdict and re-shows the Validate button.
  type CredentialValidationState = {
    status: "idle" | "validating" | "valid" | "invalid";
    error: string | null;
    models: string[] | null;
    validatedKey: string | null;
    validatedProvider: string | null;
  };
  const [credentialValidation, setCredentialValidation] =
    useState<CredentialValidationState>({
      status: "idle",
      error: null,
      models: null,
      validatedKey: null,
      validatedProvider: null,
    });

  // Any edit to the key (or switching tool/provider so a different
  // credential is required) invalidates a prior verdict. Rather than
  // sync it via a setState-in-effect (flagged by
  // react-hooks/set-state-in-effect as a cascading-render smell), we
  // derive the effective status/error below: when the stored verdict
  // does not match the current inputs, treat it as `"idle"` and hide
  // any stale error. The raw `credentialValidation` state is still the
  // source of truth for the mutation callbacks.
  const effectiveValidation = useMemo<{
    status: "idle" | "validating" | "valid" | "invalid";
    error: string | null;
  }>(() => {
    if (credentialValidation.status === "idle") {
      return { status: "idle", error: null };
    }
    if (credentialValidation.status === "validating") {
      return { status: "validating", error: null };
    }
    const trimmed = form.credentialKey.trim();
    const matchesCurrent =
      credentialValidation.validatedKey === trimmed &&
      credentialValidation.validatedProvider === requiredCredentialProvider;
    if (!matchesCurrent) {
      return { status: "idle", error: null };
    }
    return {
      status: credentialValidation.status,
      error: credentialValidation.error,
    };
  }, [credentialValidation, form.credentialKey, requiredCredentialProvider]);

  // Auto-validation is triggered from `handleNext` — there is no
  // standalone Validate button on the wizard. We use `mutateAsync` so
  // the Next handler can await the outcome and keep/advance the step
  // based on the verdict.
  const validateCredential = useMutation({
    mutationFn: async () => {
      if (requiredCredentialProvider === null) {
        throw new Error("No credential required for this selection.");
      }
      const trimmed = form.credentialKey.trim();
      if (!trimmed) {
        throw new Error("Enter an API key to validate.");
      }
      return await api.validateProviderCredential(
        requiredCredentialProvider,
        trimmed,
      );
    },
    onMutate: () => {
      setCredentialValidation((prev) => ({
        ...prev,
        status: "validating",
        error: null,
      }));
    },
    onSuccess: (result) => {
      const trimmed = form.credentialKey.trim();
      setCredentialValidation({
        status: result.valid ? "valid" : "invalid",
        error: result.valid ? null : (result.error ?? "Validation failed."),
        models: result.valid ? (result.models ?? []) : null,
        validatedKey: trimmed,
        validatedProvider: requiredCredentialProvider,
      });
      // Seed the Model field from the freshly-validated list so the
      // selection reflects what the operator's account actually
      // supports. Only overwrite when we got a non-empty list —
      // providers that return zero entries (rare) would otherwise
      // blank the dropdown.
      if (result.valid && result.models && result.models.length > 0) {
        setForm((prev) =>
          result.models!.includes(prev.model)
            ? prev
            : { ...prev, model: result.models![0] },
        );
      }
    },
    onError: (err) => {
      const message = err instanceof Error ? err.message : String(err);
      setCredentialValidation((prev) => ({
        ...prev,
        status: "invalid",
        error: message,
      }));
    },
  });

  // True when the currently-typed key has a fresh, successful verdict
  // attached. The Model dropdown gate below reads this.
  const credentialValidated = useMemo(() => {
    if (credentialValidation.status !== "valid") return false;
    const trimmed = form.credentialKey.trim();
    return (
      credentialValidation.validatedKey === trimmed &&
      credentialValidation.validatedProvider === requiredCredentialProvider
    );
  }, [credentialValidation, form.credentialKey, requiredCredentialProvider]);

  // #655: auto-validate on blur. The input's onBlur calls this; it
  // deduplicates "same key already validated" and "validation in
  // flight" so paste-then-click-next doesn't fire two requests. Parent-
  // level so the CredentialSection stays unaware of the staleness
  // rules.
  const attemptValidateCredential = useCallback(() => {
    if (requiredCredentialProvider === null) return;
    const trimmed = form.credentialKey.trim();
    if (trimmed.length === 0) return;
    if (validateCredential.isPending) return;
    if (
      credentialValidation.status === "valid" &&
      credentialValidation.validatedKey === trimmed &&
      credentialValidation.validatedProvider === requiredCredentialProvider
    ) {
      return;
    }
    validateCredential.mutate();
  }, [
    requiredCredentialProvider,
    form.credentialKey,
    credentialValidation.status,
    credentialValidation.validatedKey,
    credentialValidation.validatedProvider,
    validateCredential,
  ]);

  // #655: Model dropdown visibility. We only show the dropdown when
  // there is a live model source:
  //   - Ollama (dapr-agent + ollama) — rendered regardless (reachability
  //     banner handles failures; the dropdown is populated from
  //     ollamaModels or blank during discovery).
  //   - Tenant/unit credential is resolvable — `providerModels` was
  //     fetched with that credential, so the list is live.
  //   - Fresh validation of a user-typed key succeeded — use the model
  //     list returned by the validate endpoint.
  // Otherwise (the operator is still entering a key, or the key
  // failed to validate) we hide the Model dropdown entirely to avoid
  // asking them to pick from a stale static fallback.
  const isOllamaDapr =
    form.tool === "dapr-agent" && form.provider === "ollama";
  const tenantOrUnitResolvable = credentialStatus?.resolvable === true;
  const freshModels =
    credentialValidated &&
    credentialValidation.models &&
    credentialValidation.models.length > 0
      ? credentialValidation.models
      : null;
  const activeModelList: readonly string[] | null = useMemo(() => {
    if (isOllamaDapr) return ollamaModels;
    if (freshModels) return freshModels;
    if (tenantOrUnitResolvable && providerModels && providerModels.length > 0) {
      return providerModels;
    }
    return null;
  }, [isOllamaDapr, ollamaModels, freshModels, tenantOrUnitResolvable, providerModels]);
  const showModelDropdown = isOllamaDapr || activeModelList !== null;

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
    if (form.mode === null) return "Select a mode to continue.";
    if (form.mode === "template" && !form.templateId)
      return "Pick a template to continue.";
    if (form.mode === "yaml" && !form.yamlText.trim())
      return "Paste or upload a unit manifest to continue.";
    return null;
  };

  const validateStep3 = (): string | null => {
    // Skip is always allowed. If the user picked a connector, the wizard
    // step component must have produced a non-null config (it pushes null
    // while the form is incomplete, so this gate catches the "selected
    // but unfilled" case).
    if (form.connectorSlug !== null && form.connectorConfig === null) {
      return "Finish filling the connector configuration, or choose Skip.";
    }
    return null;
  };

  const handleNext = async () => {
    setStepError(null);
    if (step === 1) {
      const err = validateStep1();
      if (err) {
        setStepError(err);
        return;
      }
      // #655: when a credential has been typed we gate advance on its
      // live-verified validity. Blur-driven validation typically kicks
      // off on its own; the cases handleNext covers:
      //   - validation is still in flight (operator pasted and
      //     clicked Next before the blur's request returned) → stay
      //     on Step 1; Next disables via canGoNext until the mutation
      //     settles.
      //   - validation already failed for this key → stay on Step 1
      //     so the inline error is visible and the operator can edit.
      //   - no verdict yet ("idle", e.g. the operator never blurred
      //     the input) → fire the validation now and await the result.
      //   - validation already succeeded for this exact key → fall
      //     through and advance.
      if (
        requiredCredentialProvider !== null &&
        form.credentialKey.trim().length > 0
      ) {
        if (validateCredential.isPending) return;
        if (effectiveValidation.status === "invalid") return;
        if (effectiveValidation.status === "idle") {
          try {
            const result = await validateCredential.mutateAsync();
            if (!result.valid) return;
          } catch {
            return;
          }
        }
      }
    }
    if (step === 2) {
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
    if (step < 5) setStep((s) => (s + 1) as Step);
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
    // Apply Step 4 secrets after the unit exists. Each failure is
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
  // when the user skipped Step 3 OR filled it out partially (the wizard-
  // step component pushes `null` up until the form is valid). The server
  // is strict: either the binding is absent, or it's well-formed.
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
      const providerForKey = deriveRequiredCredentialProvider(
        form.tool,
        form.provider,
      );
      const trimmedKey = form.credentialKey.trim();
      const tenantWritePlanned =
        providerForKey !== null &&
        trimmedKey.length > 0 &&
        form.saveAsTenantDefault;
      if (tenantWritePlanned && providerForKey) {
        const secretName = PROVIDER_SECRET_NAMES[providerForKey];
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
        // #325: when the user has filled in a name on step 1, pass it
        // through as `unitName` so the created unit uses the caller-supplied
        // address path instead of the manifest's fixed `name`. Without
        // this, two invocations of the same template would collide on the
        // server's unique-name constraint.
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
        providerForKey !== null &&
        trimmedKey.length > 0 &&
        !form.saveAsTenantDefault
      ) {
        const secretName = PROVIDER_SECRET_NAMES[providerForKey];
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
    },
    onSuccess: ({ createdName, warnings }) => {
      if (warnings.length > 0) setSubmitWarnings(warnings);
      if (createdName) {
        // Invalidate the lists that render the new unit so the detail
        // page and dashboards pick it up on navigation.
        queryClient.invalidateQueries({ queryKey: queryKeys.units.all });
        queryClient.invalidateQueries({ queryKey: queryKeys.dashboard.all });
        toast({ title: "Unit created", description: createdName });
        router.push(`/units/${encodeURIComponent(createdName)}`);
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
      // #655: credential validation is kicked off from `handleNext` when
      // the operator advances, not from a standalone button. Disable
      // Next only while a validation is actually in flight so the user
      // can't fire a second call on top of the first; otherwise we rely
      // on `handleNext` itself to stay on the step when validation
      // fails.
      if (validateCredential.isPending) return false;
      // For YAML/template modes the manifest itself supplies the name, so we
      // don't gate advancement on form.name.
      if (form.mode === "yaml" || form.mode === "template") return true;
      return form.name.trim().length > 0;
    }
    if (step === 2) {
      if (form.mode === null) return false;
      if (form.mode === "template") return form.templateId !== null;
      if (form.mode === "yaml") return form.yamlText.trim().length > 0;
      return true;
    }
    if (step === 3) {
      // "Skip" is always allowed. If a connector is selected, require a
      // valid config payload from the connector's wizard step.
      if (form.connectorSlug === null) return true;
      return form.connectorConfig !== null;
    }
    return true;
  }, [step, form, validateCredential.isPending]);

  return (
    <div className="space-y-6">
      <Breadcrumbs
        items={[
          { label: "Units", href: "/units" },
          { label: "Create" },
        ]}
      />

      <div>
        <h1 className="text-2xl font-bold">Create a unit</h1>
        <p className="text-sm text-muted-foreground">
          Register a new unit and wire up its runtime configuration.
        </p>
      </div>

      <StepIndicator current={step} />

      {step === 1 && (
        <Card>
          <CardHeader>
            <CardTitle>Details</CardTitle>
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
                URL-safe: lowercase letters, digits, and hyphens only. Used as
                the unit&apos;s address. Optional on the template path — leave
                blank to inherit the template manifest&apos;s name, or supply a
                value to override it (see #325). Ignored when importing a
                YAML manifest — the manifest&apos;s name wins.
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
                <span className="text-sm text-muted-foreground">
                  Execution tool
                </span>
                <select
                  value={form.tool}
                  onChange={(e) => {
                    // #598: Provider dropdown only renders when the tool
                    // is dapr-agent. #641: every other tool with a finite
                    // model catalog (claude-code / codex / gemini) still
                    // shows a Model dropdown — when the tool changes we
                    // snap `model` to the tool's default so the wire
                    // payload stays coherent with the hidden Provider.
                    // `custom` has no known catalog; leave `model` alone.
                    const nextTool = e.target.value as ExecutionTool;
                    const toolProvider = getToolModelProvider(nextTool);
                    setForm((prev) => {
                      if (toolProvider !== null) {
                        return {
                          ...prev,
                          tool: nextTool,
                          model: getProvider(toolProvider).models[0],
                        };
                      }
                      return { ...prev, tool: nextTool };
                    });
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
              </label>
            </div>

            {/*
              #601 B-wide: Unit-level image + runtime defaults inherited
              by member agents. Positioned adjacent to the Tool field so
              operators configure the full launcher recipe in one grid.
              Always visible — no tool-based gating. Blank values are
              skipped entirely; the wizard only PUTs through the
              /api/v1/units/{id}/execution endpoint when at least one of
              the two is filled in, so units created without them look
              identical on the wire to pre-#601 units.
            */}
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
                  Individual agents can override this on their Execution
                  panel.
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

            {/*
              #598: Provider + Model only render when the execution tool
              is `dapr-agent`. Claude Code, Codex, and Gemini hard-code
              their own provider inside the tool CLI, so exposing a
              Provider dropdown for them is misleading. Custom tools have
              no contract for the Provider field either — a hypothetical
              future custom launcher that wants a provider selector must
              declare that explicitly (see docs/architecture/agent-runtime.md).
              When the fields are absent the form simply submits the
              current (possibly stale) values from state, matching the
              backend's "provider/model are optional hints" contract.
            */}
            {form.tool === "dapr-agent" && (
              <>
                <div className="grid grid-cols-1 gap-4 sm:grid-cols-2">
                  <label className="block space-y-1">
                    <span className="text-sm text-muted-foreground">
                      LLM Provider
                    </span>
                    <select
                      value={form.provider}
                      onChange={(e) => {
                        // Bug #258: when the provider changes, snap the model to
                        // that provider's default so we never submit a model that
                        // the selected provider doesn't support.
                        const nextProvider = getProvider(e.target.value);
                        setForm((prev) => ({
                          ...prev,
                          provider: nextProvider.id,
                          model: nextProvider.models[0],
                        }));
                      }}
                      aria-label="LLM provider"
                      className="flex h-9 w-full rounded-md border border-input bg-background px-3 py-1 text-sm shadow-sm focus-visible:outline-none focus-visible:ring-1 focus-visible:ring-ring disabled:cursor-not-allowed disabled:opacity-50"
                    >
                      {AI_PROVIDERS.map((p) => (
                        <option key={p.id} value={p.id}>
                          {p.displayName}
                        </option>
                      ))}
                    </select>
                  </label>
                </div>

                <CredentialSection
                  requiredProvider={requiredCredentialProvider}
                  status={credentialStatus}
                  statusPending={credentialStatusQuery.isPending}
                  statusError={credentialStatusQuery.isError}
                  credentialKey={form.credentialKey}
                  saveAsTenantDefault={form.saveAsTenantDefault}
                  overrideOpen={form.credentialOverrideOpen}
                  ollamaProbe={
                    form.provider === "ollama" ? credentialStatus : null
                  }
                  validationStatus={effectiveValidation.status}
                  validationError={effectiveValidation.error}
                  validationPassed={credentialValidated}
                  onValidate={attemptValidateCredential}
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

                {/*
                  #655: the Model dropdown is revealed only when a live
                  model source is available — either the tenant default
                  resolved (so `providerModels` is live), the operator
                  just validated a key (so `credentialValidation.models`
                  is live), or the Ollama provider is in use (list comes
                  from the local server). Otherwise we hide the dropdown
                  entirely so operators aren't asked to pick from a
                  stale static fallback before the wizard knows their
                  account works.
                */}
                {showModelDropdown && (
                  <div className="grid grid-cols-1 gap-4 sm:grid-cols-2">
                    <label className="block space-y-1">
                      <span className="text-sm text-muted-foreground">
                        Model
                      </span>
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
                  </div>
                )}
              </>
            )}
            {/*
              #626 + #641 + #655: tools that hard-code their provider
              (Claude Code, Codex, Gemini) render the inline credential
              surface first, then — only when a live model source is
              available — the Model dropdown below it. `custom` is
              deliberately excluded from the Model dropdown since we
              don't know its catalog.
            */}
            {form.tool !== "dapr-agent" &&
              requiredCredentialProvider !== null && (
                <CredentialSection
                  requiredProvider={requiredCredentialProvider}
                  status={credentialStatus}
                  statusPending={credentialStatusQuery.isPending}
                  statusError={credentialStatusQuery.isError}
                  credentialKey={form.credentialKey}
                  saveAsTenantDefault={form.saveAsTenantDefault}
                  overrideOpen={form.credentialOverrideOpen}
                  ollamaProbe={null}
                  validationStatus={effectiveValidation.status}
                  validationError={effectiveValidation.error}
                  validationPassed={credentialValidated}
                  onValidate={attemptValidateCredential}
                  onKeyChange={(v) => update("credentialKey", v)}
                  onToggleSaveAsTenantDefault={(v) =>
                    update("saveAsTenantDefault", v)
                  }
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

            {form.tool !== "dapr-agent" &&
              toolModelProvider !== null &&
              showModelDropdown && (
                <div className="grid grid-cols-1 gap-4 sm:grid-cols-2">
                  <label className="block space-y-1">
                    <span className="text-sm text-muted-foreground">Model</span>
                    <select
                      value={form.model}
                      onChange={(e) => update("model", e.target.value)}
                      aria-label="Model"
                      className="flex h-9 w-full rounded-md border border-input bg-background px-3 py-1 text-sm shadow-sm focus-visible:outline-none focus-visible:ring-1 focus-visible:ring-ring disabled:cursor-not-allowed disabled:opacity-50"
                    >
                      {(activeModelList ?? []).map((m) => (
                        <option key={m} value={m}>
                          {m}
                        </option>
                      ))}
                    </select>
                  </label>
                </div>
              )}

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

      {step === 3 && (
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

      {step === 4 && (
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

      {step === 5 && (
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
              <SummaryRow label="Model" value={form.model || DEFAULT_MODEL} />
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

            {missingCredential && missingCredentialMessage && (
              <p
                role="alert"
                data-testid="missing-credential-message"
                className="rounded-md border border-warning/50 bg-warning/15 px-3 py-2 text-sm text-foreground"
              >
                {missingCredentialMessage}
              </p>
            )}

            <Button
              onClick={handleCreate}
              disabled={submitting || missingCredential}
              data-testid="create-unit-button"
            >
              {submitting ? "Creating…" : "Create unit"}
            </Button>
          </CardContent>
        </Card>
      )}

      <div className="flex items-center justify-between">
        <Button
          variant="outline"
          onClick={handleBack}
          disabled={step === 1 || submitting}
        >
          Back
        </Button>
        {step < 5 && (
          <Button
            onClick={() => {
              void handleNext();
            }}
            disabled={!canGoNext}
          >
            {step === 1 && validateCredential.isPending ? "Validating…" : "Next"}
          </Button>
        )}
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
      className={cn(
        "flex w-full items-start gap-3 rounded-md border p-4 text-left transition-colors",
        selected
          ? "border-primary bg-primary/5"
          : "border-border hover:bg-accent/50",
      )}
    >
      <div
        className={cn(
          "mt-0.5 rounded-md bg-muted p-2",
          selected && "bg-primary/15 text-primary",
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
  // #655: wizard-time credential validation. The verdict is surfaced
  // inline; validation itself fires from the input's `onBlur` (the
  // operator types a key, tabs or clicks away, and the wizard posts
  // the key to the provider). There is no manual Validate button.
  //   - `validationStatus`: lifecycle of the validate mutation for the
  //     current key. `"idle"` is the resting state; `"validating"` is
  //     in flight; `"valid"` / `"invalid"` reflect the provider verdict.
  //   - `validationError`: operator-facing message from the server on
  //     failure. Rendered inline under the input.
  //   - `validationPassed`: convenience flag that pins the "valid"
  //     verdict to the exact typed key + provider pair; stale passes
  //     (key edited after a previous validation) do not advance.
  //   - `onValidate`: invoked from the input's `onBlur`. The parent is
  //     responsible for skipping duplicate / in-flight requests.
  validationStatus: "idle" | "validating" | "valid" | "invalid";
  validationError: string | null;
  validationPassed: boolean;
  onValidate: () => void;
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
    validationStatus,
    validationError,
    validationPassed,
    onValidate,
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
    return (
      <p className="text-xs text-muted-foreground" role="status">
        Could not verify {displayName} credentials.
      </p>
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
              validationStatus={validationStatus}
              validationError={validationError}
              validationPassed={validationPassed}
              onValidate={onValidate}
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
        validationStatus={validationStatus}
        validationError={validationError}
        validationPassed={validationPassed}
        onValidate={onValidate}
      />
    </div>
  );
}

/**
 * The shared input + "show / hide" toggle + "Save as tenant default"
 * checkbox used by both the "not configured" and "override" flows.
 * Extracted so the two call sites cannot drift apart on labelling or
 * accessibility attributes.
 */
function CredentialInputControls({
  provider,
  credentialKey,
  saveAsTenantDefault,
  onKeyChange,
  onToggleSaveAsTenantDefault,
  tenantToggleLabel,
  validationStatus,
  validationError,
  validationPassed,
  onValidate,
}: {
  provider: "anthropic" | "openai" | "google";
  credentialKey: string;
  saveAsTenantDefault: boolean;
  onKeyChange: (value: string) => void;
  onToggleSaveAsTenantDefault: (value: boolean) => void;
  tenantToggleLabel: string;
  validationStatus: "idle" | "validating" | "valid" | "invalid";
  validationError: string | null;
  validationPassed: boolean;
  onValidate: () => void;
}) {
  const [show, setShow] = useState(false);
  const inputId = `credential-key-${provider}`;
  const toggleId = `credential-save-tenant-${provider}`;
  const inputType = show ? "text" : "password";
  const displayName = providerLabel(provider);

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
        <span className="text-xs text-muted-foreground">
          {fieldLabel}
        </span>
        <div className="flex items-center gap-2">
          <Input
            id={inputId}
            type={inputType}
            value={credentialKey}
            onChange={(e) => onKeyChange(e.target.value)}
            onBlur={onValidate}
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

      {/*
        #655: validation runs automatically when the operator clicks
        Next — no manual button. We still surface the verdict inline
        so the wizard explains why Next stayed on this step (failure)
        or why the Model dropdown suddenly changed (success).
      */}
      {validationStatus === "validating" && (
        <p
          role="status"
          data-testid="credential-validation-in-flight"
          className="text-xs text-muted-foreground"
        >
          Validating {displayName} API key…
        </p>
      )}
      {validationPassed && (
        <p
          role="status"
          data-testid="credential-validation-success"
          className="flex items-start gap-1.5 text-xs text-emerald-700 dark:text-emerald-300"
        >
          <CheckCircle2 className="mt-0.5 h-3.5 w-3.5 shrink-0" aria-hidden />
          <span>
            {displayName} accepted the key. Model list refreshed from your
            account.
          </span>
        </p>
      )}
      {validationStatus === "invalid" && validationError && (
        <p
          role="alert"
          data-testid="credential-validation-error"
          className="flex items-start gap-1.5 text-xs text-destructive"
        >
          <AlertTriangle className="mt-0.5 h-3.5 w-3.5 shrink-0" aria-hidden />
          <span>{validationError}</span>
        </p>
      )}

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
