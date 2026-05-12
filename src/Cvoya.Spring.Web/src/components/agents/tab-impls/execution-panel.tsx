"use client";

import { useMemo, useState } from "react";
import Link from "next/link";
import { useMutation, useQueryClient } from "@tanstack/react-query";
import { AlertTriangle, CheckCircle2, Container, Trash2 } from "lucide-react";

import { Badge } from "@/components/ui/badge";
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
import { api } from "@/lib/api/client";
import {
  useAgentExecution,
  useModelProviderModels,
  useModelProviders,
  useProviderCredentialStatus,
  useUnitExecution,
} from "@/lib/api/queries";
import { loadImageHistory } from "@/lib/image-history";
import { queryKeys } from "@/lib/api/query-keys";
import { formatTranslatedError } from "@/lib/api/translate-error";
import type {
  AgentExecutionResponse,
  UnitExecutionResponse,
} from "@/lib/api/types";
import {
  HOSTING_MODES,
  RUNTIME_LIST,
  getAllowedProviders,
  getFixedProvider,
  isRuntimeProviderFixed,
  type HostingMode,
} from "@/lib/ai-models";
import {
  providerDisplayName,
  runtimeCredentialDescriptor,
  type RuntimeCredentialDescriptor,
} from "@/lib/runtime-credentials";

/**
 * Agent Execution panel (ADR-0038).
 *
 * Symmetric to the unit-side Execution tab plus the agent-exclusive
 * `hosting` slot. Overlays the owning unit's execution defaults via
 * `useUnitExecution(parentUnit)` so the operator sees the inherited
 * value as an italic grey placeholder when the agent field is blank.
 *
 * Local form state mirrors the wire shape directly — `{image, runtime,
 * model: {provider, id}, hosting}` — so save / clear handlers hand the
 * form straight to the API. ADR-0038 §1's conditional provider picker:
 *   - fixed-provider runtime → picker hidden, model dropdown filtered.
 *   - multi-provider runtime → picker shown as a model-list filter
 *     against the tenant's installed model providers.
 */

interface ExecutionPanelProps {
  agentId: string;
  parentUnitId: string | null;
}

const FIELD_UNSET = "__unset__";

type ExecutionField = "image" | "runtime" | "providerOnly" | "model" | "hosting";

function isEmpty(block: AgentExecutionResponse): boolean {
  return (
    !block.image &&
    !block.runtime &&
    !block.model &&
    !block.hosting
  );
}

function persistedToForm(
  persisted: AgentExecutionResponse | null,
): AgentExecutionResponse {
  return {
    image: persisted?.image ?? null,
    runtime: persisted?.runtime ?? null,
    model: persisted?.model ?? null,
    hosting: persisted?.hosting ?? null,
  };
}

export function AgentExecutionPanel({
  agentId,
  parentUnitId,
}: ExecutionPanelProps) {
  const { toast } = useToast();
  const queryClient = useQueryClient();
  const agentExecutionQuery = useAgentExecution(agentId);
  const unitExecutionQuery = useUnitExecution(parentUnitId ?? "", {
    enabled: Boolean(parentUnitId),
  });
  const installedProvidersQuery = useModelProviders();
  const installedProviders = useMemo(
    () => installedProvidersQuery.data ?? [],
    [installedProvidersQuery.data],
  );

  const persisted = agentExecutionQuery.data ?? null;
  const unitDefaults: UnitExecutionResponse | null =
    unitExecutionQuery.data ?? null;

  const [form, setForm] = useState<AgentExecutionResponse>(() =>
    persistedToForm(null),
  );
  const [seededFor, setSeededFor] = useState<string | null>(null);
  const [imageHistory] = useState(() => loadImageHistory());
  const fingerprint = useMemo(
    () => JSON.stringify(persisted ?? null),
    [persisted],
  );
  if (fingerprint !== seededFor) {
    setForm(persistedToForm(persisted));
    setSeededFor(fingerprint);
  }

  const dirty = useMemo(() => {
    const current = persistedToForm(persisted);
    return (
      form.image !== current.image ||
      form.runtime !== current.runtime ||
      (form.model?.provider ?? null) !== (current.model?.provider ?? null) ||
      (form.model?.id ?? null) !== (current.model?.id ?? null) ||
      form.hosting !== current.hosting
    );
  }, [form, persisted]);

  // Effective runtime for gating: agent's own value wins; unit default
  // fills in otherwise.
  const effectiveRuntime =
    form.runtime ?? persisted?.runtime ?? unitDefaults?.runtime ?? null;
  const providerFixed = isRuntimeProviderFixed(effectiveRuntime);
  const fixedProvider = getFixedProvider(effectiveRuntime);
  const allowedProviders = getAllowedProviders(effectiveRuntime);

  // Effective provider for the model dropdown.
  const effectiveProvider = providerFixed
    ? fixedProvider
    : (form.model?.provider ??
        persisted?.model?.provider ??
        unitDefaults?.model?.provider ??
        null);
  const credentialDescriptor = useMemo(
    () => runtimeCredentialDescriptor(effectiveRuntime, effectiveProvider),
    [effectiveRuntime, effectiveProvider],
  );

  const modelsQuery = useModelProviderModels(effectiveProvider ?? "", {
    enabled: effectiveProvider !== null,
  });
  const providerModels = modelsQuery.data?.map((m) => m.id) ?? null;

  const providerOptions = useMemo<string[]>(() => {
    if (providerFixed) return [];
    const installed = installedProviders.map((p) => p.id);
    if (allowedProviders === null || allowedProviders.length === 0) {
      return installed;
    }
    return installed.filter((id) =>
      (allowedProviders as readonly string[]).includes(id),
    );
  }, [providerFixed, allowedProviders, installedProviders]);

  const setMutation = useMutation({
    mutationFn: async (
      next: AgentExecutionResponse,
    ): Promise<AgentExecutionResponse> => {
      if (isEmpty(next)) {
        await api.clearAgentExecution(agentId);
        return {};
      }
      return await api.setAgentExecution(agentId, next);
    },
    onSuccess: (updated) => {
      queryClient.setQueryData(queryKeys.agents.execution(agentId), updated);
      toast({ title: "Execution block saved" });
    },
    onError: (err) => {
      toast({
        title: "Save failed",
        description: formatTranslatedError(err),
        variant: "destructive",
      });
    },
  });

  const clearAllMutation = useMutation({
    mutationFn: async (): Promise<AgentExecutionResponse> => {
      await api.clearAgentExecution(agentId);
      return {};
    },
    onSuccess: (cleared) => {
      queryClient.setQueryData(queryKeys.agents.execution(agentId), cleared);
      toast({ title: "Execution block cleared" });
    },
    onError: (err) => {
      toast({
        title: "Clear failed",
        description: formatTranslatedError(err),
        variant: "destructive",
      });
    },
  });

  const setModelField = (key: "provider" | "id", value: string | null) => {
    setForm((prev) => {
      const nextProvider =
        key === "provider" ? value : (prev.model?.provider ?? null);
      const nextId = key === "id" ? value : (prev.model?.id ?? null);
      const model =
        nextProvider && nextId
          ? { provider: nextProvider, id: nextId }
          : null;
      return { ...prev, model };
    });
  };

  const setRuntime = (next: string | null) => {
    setForm((prev) => {
      if (next === null) return { ...prev, runtime: null, model: null };
      const fixed = getFixedProvider(next);
      const nextProvider = fixed ?? null;
      const prevProvider = prev.model?.provider ?? null;
      const keepModelId =
        prevProvider !== null && prevProvider === nextProvider
          ? (prev.model?.id ?? null)
          : null;
      const model =
        nextProvider && keepModelId
          ? { provider: nextProvider, id: keepModelId }
          : null;
      return { ...prev, runtime: next, model };
    });
  };

  const clearField = (field: ExecutionField) => {
    let next = form;
    if (field === "image") next = { ...form, image: null };
    if (field === "runtime") next = { ...form, runtime: null, model: null };
    if (field === "providerOnly") next = { ...form, model: null };
    if (field === "model") next = { ...form, model: null };
    if (field === "hosting") next = { ...form, hosting: null };
    setForm(next);
    setMutation.mutate(next);
  };

  const handleSave = () => {
    setMutation.mutate(form);
  };

  if (agentExecutionQuery.isPending) {
    return <Skeleton className="h-64" />;
  }

  const hasAny = persisted !== null && !isEmpty(persisted);

  // Inherited-value helpers — unit-default fall-back when the agent
  // leaves the slot blank. Surface as italic placeholders so the
  // overlay is visible to keyboard / screen-reader users.
  const inherited = (slot: ExecutionField): string | null => {
    if (!unitDefaults) return null;
    switch (slot) {
      case "image":
        return form.image ? null : (unitDefaults.image ?? null);
      case "runtime":
        return form.runtime ? null : (unitDefaults.runtime ?? null);
      case "providerOnly":
        return form.model?.provider
          ? null
          : (unitDefaults.model?.provider ?? null);
      case "model":
        return form.model?.id
          ? null
          : (unitDefaults.model?.id ?? null);
      case "hosting":
        return null; // Unit defaults don't carry a hosting slot.
      default:
        return null;
    }
  };

  return (
    <Card data-testid="agent-execution-panel">
      <CardHeader className="flex flex-row items-center justify-between gap-2 space-y-0 pb-2">
        <CardTitle className="flex items-center gap-2 text-base">
          <Container className="h-4 w-4" />
          <span>Execution</span>
          {hasAny ? (
            <Badge variant="default" className="ml-2 text-xs font-normal">
              Configured
            </Badge>
          ) : (
            <Badge variant="outline" className="ml-2 text-xs font-normal">
              Inherits
            </Badge>
          )}
        </CardTitle>
        {hasAny && (
          <Button
            size="sm"
            variant="outline"
            onClick={() => clearAllMutation.mutate()}
            disabled={clearAllMutation.isPending}
            aria-label="Clear agent execution block"
          >
            Clear all
          </Button>
        )}
      </CardHeader>
      <CardContent className="space-y-4 text-sm">
        <p className="text-xs text-muted-foreground">
          Agent-level overrides for the agent image, runtime, and model.
          Any field left blank inherits from the owning unit
          {parentUnitId ? (
            <>
              {" "}
              (
              <Link
                href={`/units?node=${encodeURIComponent(parentUnitId)}&tab=Overview`}
                className="underline"
              >
                {parentUnitId}
              </Link>
              )
            </>
          ) : null}
          ; the dispatcher merges the unit default at runtime.
        </p>

        <FieldRow
          label="Image"
          help={
            inherited("image")
              ? `inherited from unit: ${inherited("image")}`
              : "Default container image reference."
          }
          onClear={persisted?.image ? () => clearField("image") : undefined}
          busy={setMutation.isPending}
        >
          <datalist id="agent-execution-image-suggestions">
            {imageHistory.map((ref) => (
              <option key={ref} value={ref} />
            ))}
          </datalist>
          <Input
            value={form.image ?? ""}
            onChange={(e) =>
              setForm((prev) => ({
                ...prev,
                image: e.target.value ? e.target.value : null,
              }))
            }
            placeholder={
              inherited("image")
                ? `inherited from unit: ${inherited("image")}`
                : "ghcr.io/cvoya-com/spring-voyage-claude-code-base:latest"
            }
            list={imageHistory.length > 0 ? "agent-execution-image-suggestions" : undefined}
            aria-label="Agent execution image"
            data-testid="agent-execution-image-input"
            className={
              !form.image && inherited("image")
                ? "italic text-muted-foreground placeholder:italic placeholder:text-muted-foreground"
                : undefined
            }
          />
        </FieldRow>

        <FieldRow
          label="Agent Runtime"
          help={
            inherited("runtime")
              ? `inherited from unit: ${inherited("runtime")}`
              : "Agent runtime the dispatcher uses."
          }
          onClear={persisted?.runtime ? () => clearField("runtime") : undefined}
          busy={setMutation.isPending}
        >
          <RuntimeSelect
            value={form.runtime ?? null}
            onChange={setRuntime}
            inheritedLabel={inherited("runtime")}
            ariaLabel="Agent runtime"
            testid="agent-execution-runtime-select"
          />
        </FieldRow>

        {/* Conditional Model Provider — hidden when the runtime fixes
            its provider (claude-code / codex / gemini). The `custom`
            slot is reserved for v0.2 (empty allow-list) — keep the
            picker hidden until that lands. */}
        {!providerFixed &&
          effectiveRuntime !== null &&
          effectiveRuntime !== "custom" && (
          <FieldRow
            label="Model Provider"
            help={
              inherited("providerOnly")
                ? `inherited from unit: ${inherited("providerOnly")}`
                : providerOptions.length === 0
                  ? "No installed model providers match this runtime. Install one with `spring model-provider install <id>`."
                  : "LLM provider that hosts the model the runtime dispatches against."
            }
            onClear={
              form.model?.provider ? () => clearField("providerOnly") : undefined
            }
            busy={setMutation.isPending}
          >
            <SelectField
              value={form.model?.provider ?? null}
              onChange={(next) => setModelField("provider", next)}
              options={providerOptions}
              inheritedLabel={inherited("providerOnly")}
              ariaLabel="Model provider"
              testid="agent-execution-provider-select"
            />
          </FieldRow>
        )}

        <FieldRow
          label="Model"
          help={
            inherited("model")
              ? `inherited from unit: ${inherited("model")}`
              : effectiveProvider === null
                ? "Pick an Agent Runtime (and Model Provider, if multi-provider) to load the model catalogue."
                : "Model identifier within the selected provider's catalogue."
          }
          onClear={form.model?.id ? () => clearField("model") : undefined}
          busy={setMutation.isPending}
        >
          {effectiveProvider !== null &&
          providerModels &&
          providerModels.length > 0 ? (
            <select
              value={form.model?.id ?? ""}
              onChange={(e) =>
                setModelField("id", e.target.value ? e.target.value : null)
              }
              aria-label="Agent execution model"
              data-testid="agent-execution-model-select"
              className="flex h-9 w-full rounded-md border border-input bg-background px-3 text-sm shadow-sm focus-visible:outline-none focus-visible:ring-1 focus-visible:ring-ring"
            >
              <option value="">
                {inherited("model")
                  ? `inherited: ${inherited("model")}`
                  : "(leave to default)"}
              </option>
              {providerModels.map((m) => (
                <option key={m} value={m}>
                  {m}
                </option>
              ))}
            </select>
          ) : (
            <Input
              value={form.model?.id ?? ""}
              onChange={(e) =>
                setModelField("id", e.target.value ? e.target.value : null)
              }
              placeholder={
                inherited("model")
                  ? `inherited from unit: ${inherited("model")}`
                  : "e.g. claude-sonnet-4-6"
              }
              aria-label="Agent execution model"
              data-testid="agent-execution-model-input"
              disabled={effectiveProvider === null}
              className={
                !form.model?.id && inherited("model")
                  ? "italic text-muted-foreground placeholder:italic placeholder:text-muted-foreground"
                  : undefined
              }
            />
          )}
        </FieldRow>

        {effectiveProvider !== null && (
          <CredentialStatusBanner
            providerId={effectiveProvider}
            credential={credentialDescriptor}
          />
        )}

        {/* Hosting — agent-exclusive. Unit defaults don't carry a
            hosting slot so there's nothing to inherit from. */}
        <FieldRow
          label="Hosting"
          help="Agent lifecycle — ephemeral launches per-message; persistent runs continuously. Mirrors `spring agent execution set <id> --hosting`."
          onClear={persisted?.hosting ? () => clearField("hosting") : undefined}
          busy={setMutation.isPending}
        >
          <HostingSelect
            value={(form.hosting ?? null) as HostingMode | null}
            onChange={(next) =>
              setForm((prev) => ({ ...prev, hosting: next }))
            }
            ariaLabel="Agent hosting mode"
            testid="agent-execution-hosting-select"
          />
        </FieldRow>

        <div className="flex items-center justify-end gap-2 pt-2">
          {dirty && (
            <span className="text-xs text-muted-foreground">
              Unsaved changes
            </span>
          )}
          <Button
            size="sm"
            onClick={handleSave}
            disabled={!dirty || setMutation.isPending}
          >
            {setMutation.isPending ? "Saving…" : "Save"}
          </Button>
        </div>
      </CardContent>
    </Card>
  );
}

interface FieldRowProps {
  label: string;
  help: string;
  onClear?: () => void;
  busy: boolean;
  children: React.ReactNode;
}

function FieldRow({ label, help, onClear, busy, children }: FieldRowProps) {
  return (
    <div className="space-y-1">
      <div className="flex items-center justify-between gap-2">
        <span className="text-sm text-muted-foreground">{label}</span>
        {onClear && (
          <Button
            size="sm"
            variant="ghost"
            onClick={onClear}
            disabled={busy}
            className="h-7 px-2 text-xs"
            aria-label={`Clear agent ${label.toLowerCase()}`}
            data-testid={`agent-execution-clear-${label.toLowerCase().replace(/\s+/g, "-")}`}
          >
            <Trash2 className="mr-1 h-3 w-3" />
            Clear
          </Button>
        )}
      </div>
      {children}
      <p
        className={
          help.startsWith("inherited")
            ? "text-xs italic text-muted-foreground"
            : "text-xs text-muted-foreground"
        }
        data-testid={
          help.startsWith("inherited") ? "inherit-indicator" : undefined
        }
      >
        {help}
      </p>
    </div>
  );
}

interface RuntimeSelectProps {
  value: string | null;
  onChange: (next: string | null) => void;
  inheritedLabel: string | null;
  ariaLabel: string;
  testid: string;
}

function RuntimeSelect({
  value,
  onChange,
  inheritedLabel,
  ariaLabel,
  testid,
}: RuntimeSelectProps) {
  return (
    <select
      value={value ?? FIELD_UNSET}
      onChange={(e) => {
        const next = e.target.value;
        onChange(next === FIELD_UNSET ? null : next);
      }}
      aria-label={ariaLabel}
      data-testid={testid}
      className={
        "flex h-9 w-full rounded-md border border-input bg-background px-3 text-sm shadow-sm focus-visible:outline-none focus-visible:ring-1 focus-visible:ring-ring" +
        (value === null && inheritedLabel
          ? " italic text-muted-foreground"
          : "")
      }
    >
      <option value={FIELD_UNSET}>
        {inheritedLabel ? `inherited: ${inheritedLabel}` : "(leave to default)"}
      </option>
      {RUNTIME_LIST.map((r) => (
        <option key={r.id} value={r.id}>
          {r.displayName}
        </option>
      ))}
    </select>
  );
}

interface SelectFieldProps {
  value: string | null;
  onChange: (next: string | null) => void;
  options: readonly string[];
  inheritedLabel: string | null;
  ariaLabel: string;
  testid: string;
}

function SelectField({
  value,
  onChange,
  options,
  inheritedLabel,
  ariaLabel,
  testid,
}: SelectFieldProps) {
  return (
    <select
      value={value ?? FIELD_UNSET}
      onChange={(e) => {
        const next = e.target.value;
        onChange(next === FIELD_UNSET ? null : next);
      }}
      aria-label={ariaLabel}
      data-testid={testid}
      className={
        "flex h-9 w-full rounded-md border border-input bg-background px-3 text-sm shadow-sm focus-visible:outline-none focus-visible:ring-1 focus-visible:ring-ring" +
        (value === null && inheritedLabel
          ? " italic text-muted-foreground"
          : "")
      }
    >
      <option value={FIELD_UNSET}>
        {inheritedLabel ? `inherited: ${inheritedLabel}` : "(leave to default)"}
      </option>
      {options.map((opt) => (
        <option key={opt} value={opt}>
          {opt}
        </option>
      ))}
    </select>
  );
}

interface HostingSelectProps {
  value: HostingMode | null;
  onChange: (next: HostingMode | null) => void;
  ariaLabel: string;
  testid: string;
}

function HostingSelect({
  value,
  onChange,
  ariaLabel,
  testid,
}: HostingSelectProps) {
  return (
    <select
      value={value ?? FIELD_UNSET}
      onChange={(e) => {
        const next = e.target.value;
        onChange(next === FIELD_UNSET ? null : (next as HostingMode));
      }}
      aria-label={ariaLabel}
      data-testid={testid}
      className="flex h-9 w-full rounded-md border border-input bg-background px-3 text-sm shadow-sm focus-visible:outline-none focus-visible:ring-1 focus-visible:ring-ring"
    >
      <option value={FIELD_UNSET}>(leave to default)</option>
      {HOSTING_MODES.map((m) => (
        <option key={m.id} value={m.id}>
          {m.label}
        </option>
      ))}
    </select>
  );
}

/**
 * Credential-status banner — identical palette to the wizard Step 1
 * and the unit Execution tab so the three surfaces share one axe sweep.
 */
function CredentialStatusBanner({
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

  if (isError || !data) {
    return (
      <p className="text-xs text-muted-foreground" role="status">
        Could not verify {credential?.label ?? providerDisplayName(providerId)}.
      </p>
    );
  }

  const displayName = credential?.label ?? providerDisplayName(providerId);

  if (data.resolvable) {
    const originHint =
      data.source === "unit"
        ? `${displayName}: set on unit`
        : data.source === "tenant"
          ? `${displayName}: inherited from tenant default`
          : providerId === "ollama"
            ? `${displayName} reachable`
            : `${displayName} resolvable`;
    return (
      <div
        role="status"
        data-testid="agent-execution-credential-status"
        data-resolvable="true"
        data-source={data.source ?? ""}
        className="flex items-start gap-2 rounded-md border border-emerald-500/40 bg-emerald-500/10 px-3 py-2 text-sm text-emerald-900 dark:text-emerald-200"
      >
        <CheckCircle2 className="mt-0.5 h-4 w-4 shrink-0" aria-hidden />
        <span>{originHint}</span>
      </div>
    );
  }

  return (
    <div
      role="alert"
      data-testid="agent-execution-credential-status"
      data-resolvable="false"
      className="flex items-start gap-2 rounded-md border border-warning/50 bg-warning/15 px-3 py-2 text-sm text-warning"
    >
      <AlertTriangle className="mt-0.5 h-4 w-4 shrink-0" aria-hidden />
      <div className="flex-1 text-foreground">
        {providerId === "ollama" ? (
          <p>
            {data.suggestion ??
              "Ollama not reachable. Check that the Ollama server is running."}
          </p>
        ) : (
          <p>
            {displayName}: not configured.{" "}
            <Link
              href="/?drawer=settings"
              className="font-medium underline underline-offset-2"
            >
              Configure in Settings → Tenant defaults
            </Link>
          </p>
        )}
      </div>
    </div>
  );
}
