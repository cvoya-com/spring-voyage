"use client";

import { useCallback, useEffect, useMemo, useState } from "react";
import { useRouter } from "next/navigation";
import Link from "next/link";
import {
  ArrowLeft,
  Check,
  FileCode,
  FileText,
  KeyRound,
  Plug,
  Rocket,
  Sparkles,
} from "lucide-react";

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
import type {
  ConnectorTypeResponse,
  UnitConnectorBindingRequest,
  UnitTemplateSummary,
} from "@/lib/api/types";
import {
  AI_PROVIDERS,
  DEFAULT_EXECUTION_TOOL,
  DEFAULT_HOSTING_MODE,
  DEFAULT_MODEL,
  DEFAULT_PROVIDER_ID,
  EXECUTION_TOOLS,
  HOSTING_MODES,
  getProvider,
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
  mode: null,
  templateId: null,
  yamlText: "",
  yamlFileName: null,
  secrets: [],
  connectorSlug: null,
  connectorTypeId: null,
  connectorConfig: null,
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
  const [step, setStep] = useState<Step>(1);
  const [form, setForm] = useState<FormState>(INITIAL_FORM);
  const [stepError, setStepError] = useState<string | null>(null);
  const [submitError, setSubmitError] = useState<string | null>(null);
  const [submitWarnings, setSubmitWarnings] = useState<string[]>([]);
  const [submitting, setSubmitting] = useState(false);

  // Template catalog state (#119): fetched once when the wizard mounts so the
  // Template card can render a picker without a per-click round-trip.
  const [templates, setTemplates] = useState<UnitTemplateSummary[] | null>(null);
  const [templatesError, setTemplatesError] = useState<string | null>(null);
  const [templatesLoading, setTemplatesLoading] = useState(false);

  // Connector catalog state (#199): fetched once, lists every connector the
  // server knows about so Step 3 can let the user pick one without a
  // per-click round-trip. Connectors that don't ship a web component still
  // show up, but their selector surfaces a fallback hint.
  const [connectorTypes, setConnectorTypes] = useState<
    ConnectorTypeResponse[] | null
  >(null);
  const [connectorTypesError, setConnectorTypesError] = useState<string | null>(
    null,
  );

  // #350: Ollama model discovery — fetched when dapr-agent + ollama is selected.
  const [ollamaModels, setOllamaModels] = useState<string[] | null>(null);
  const [ollamaModelsLoading, setOllamaModelsLoading] = useState(false);

  useEffect(() => {
    let cancelled = false;
    setTemplatesLoading(true);
    api
      .listUnitTemplates()
      .then((list) => {
        if (cancelled) return;
        setTemplates(list);
        setTemplatesError(null);
      })
      .catch((err) => {
        if (cancelled) return;
        const message = err instanceof Error ? err.message : String(err);
        setTemplatesError(message);
        setTemplates([]);
      })
      .finally(() => {
        if (!cancelled) setTemplatesLoading(false);
      });
    return () => {
      cancelled = true;
    };
  }, []);

  useEffect(() => {
    let cancelled = false;
    api
      .listConnectors()
      .then((list) => {
        if (cancelled) return;
        setConnectorTypes(list);
        setConnectorTypesError(null);
      })
      .catch((err) => {
        if (cancelled) return;
        const message = err instanceof Error ? err.message : String(err);
        setConnectorTypesError(message);
        setConnectorTypes([]);
      });
    return () => {
      cancelled = true;
    };
  }, []);

  // #350: fetch Ollama models when dapr-agent + ollama is selected.
  useEffect(() => {
    if (form.tool !== "dapr-agent" || form.provider !== "ollama") {
      setOllamaModels(null);
      return;
    }
    let cancelled = false;
    setOllamaModelsLoading(true);
    api
      .listOllamaModels()
      .then((models) => {
        if (cancelled) return;
        setOllamaModels(models.map((m) => m.name));
      })
      .catch(() => {
        if (cancelled) return;
        // Fall back to static catalog on failure.
        setOllamaModels(null);
      })
      .finally(() => {
        if (!cancelled) setOllamaModelsLoading(false);
      });
    return () => {
      cancelled = true;
    };
  }, [form.tool, form.provider]);

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

  const handleCreate = async () => {
    setSubmitError(null);
    setSubmitWarnings([]);
    setSubmitting(true);
    try {
      let createdName: string | null = null;
      const warnings: string[] = [];
      const connector = buildConnectorBinding();

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
          setSubmitError("Selected template is no longer available.");
          return;
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

      if (warnings.length > 0) setSubmitWarnings(warnings);
      if (createdName) {
        toast({ title: "Unit created", description: createdName });
        router.push(`/units/${encodeURIComponent(createdName)}`);
      }
    } catch (err) {
      const message = err instanceof Error ? err.message : String(err);
      setSubmitError(message);
      toast({
        title: "Failed to create unit",
        description: message,
        variant: "destructive",
      });
    } finally {
      setSubmitting(false);
    }
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
  }, [step, form]);

  return (
    <div className="space-y-6">
      <Link
        href="/units"
        className="inline-flex items-center gap-1 text-sm text-muted-foreground hover:text-foreground"
      >
        <ArrowLeft className="h-4 w-4" /> Units
      </Link>

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
                    const nextTool = e.target.value as ExecutionTool;
                    setForm((prev) => ({
                      ...prev,
                      tool: nextTool,
                      // Reset provider/model when switching away from dapr-agent
                      ...(nextTool !== "dapr-agent"
                        ? {
                            provider: DEFAULT_PROVIDER_ID,
                            model: DEFAULT_MODEL,
                          }
                        : {}),
                    }));
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

            <div className="grid grid-cols-1 gap-4 sm:grid-cols-2">
              <label className="block space-y-1">
                <span className="text-sm text-muted-foreground">
                  {form.tool === "dapr-agent"
                    ? "LLM Provider"
                    : "Provider"}
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
                  aria-label="AI provider"
                  className="flex h-9 w-full rounded-md border border-input bg-background px-3 py-1 text-sm shadow-sm focus-visible:outline-none focus-visible:ring-1 focus-visible:ring-ring disabled:cursor-not-allowed disabled:opacity-50"
                >
                  {AI_PROVIDERS.map((p) => (
                    <option key={p.id} value={p.id}>
                      {p.displayName}
                    </option>
                  ))}
                </select>
              </label>

              <label className="block space-y-1">
                <span className="text-sm text-muted-foreground">Model</span>
                <select
                  value={form.model}
                  onChange={(e) => update("model", e.target.value)}
                  aria-label="Model"
                  disabled={
                    form.tool === "dapr-agent" &&
                    form.provider === "ollama" &&
                    ollamaModelsLoading
                  }
                  className="flex h-9 w-full rounded-md border border-input bg-background px-3 py-1 text-sm shadow-sm focus-visible:outline-none focus-visible:ring-1 focus-visible:ring-ring disabled:cursor-not-allowed disabled:opacity-50"
                >
                  {(form.tool === "dapr-agent" &&
                  form.provider === "ollama" &&
                  ollamaModels
                    ? ollamaModels
                    : getProvider(form.provider).models.slice()
                  ).map((m) => (
                    <option key={m} value={m}>
                      {m}
                    </option>
                  ))}
                </select>
                {form.tool === "dapr-agent" &&
                  form.provider === "ollama" &&
                  ollamaModelsLoading && (
                    <span className="block text-xs text-muted-foreground">
                      Loading models from Ollama server...
                    </span>
                  )}
              </label>
            </div>

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

            <Button onClick={handleCreate} disabled={submitting}>
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
          <Button onClick={handleNext} disabled={!canGoNext}>
            Next
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
