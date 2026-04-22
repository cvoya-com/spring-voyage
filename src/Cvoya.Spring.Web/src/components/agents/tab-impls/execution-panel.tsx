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
  useAgentRuntimeModels,
  useProviderCredentialStatus,
  useUnitExecution,
} from "@/lib/api/queries";
import { queryKeys } from "@/lib/api/query-keys";
import type {
  AgentExecutionResponse,
  UnitExecutionResponse,
} from "@/lib/api/types";
import {
  EXECUTION_HOSTING_MODES,
  EXECUTION_PROVIDERS,
  EXECUTION_RUNTIMES,
  EXECUTION_TOOL_KEYS,
} from "@/lib/api/types";
import { getToolRuntimeId, type ExecutionTool } from "@/lib/ai-models";

/**
 * #735: the Execution-surface Provider dropdown standardises on the
 * canonical names (`anthropic`/`openai`/`google`/`ollama`), while the
 * agent-runtimes endpoint keys on the runtime id (`claude` for the
 * Anthropic backend). Collapse the provider-string space to a runtime
 * id so the Model dropdown below can drive `useAgentRuntimeModels`
 * directly — the hook returns `null` when the runtime isn't installed
 * on the tenant, which we surface as a free-text Model input.
 */
function providerToRuntimeId(provider: string): string | null {
  const normalised = provider.trim().toLowerCase();
  if (!normalised) return null;
  switch (normalised) {
    case "claude":
    case "anthropic":
      return "claude";
    case "openai":
      return "openai";
    case "google":
    case "gemini":
    case "googleai":
      return "google";
    case "ollama":
      return "ollama";
    default:
      return null;
  }
}

/**
 * Agent Execution panel (#601 / #603 / #409 B-wide, portal half).
 *
 * Symmetric to the unit-side Execution tab plus the agent-exclusive
 * `hosting` slot. The panel overlays the owning unit's execution
 * defaults (via `useUnitExecution(parentUnit)`) so the operator sees
 * the inherited value as an italic grey placeholder when the agent
 * field is blank. Clicking into a field clears the placeholder and
 * lets the operator type their own value; leaving the field blank on
 * save persists `null` on the agent block, and the dispatcher merges
 * the unit default at runtime (PR #628).
 *
 * Inherit indicator is implemented at the **component level** rather
 * than via CSS-only placeholder text so that:
 *   - The inheritance source (unit name + field) remains screen-reader
 *     accessible via the help copy alongside the input.
 *   - Dropdown controls (runtime / tool / provider) can also render
 *     the inherited label, which a plain `placeholder` attribute on
 *     `<input>` cannot do.
 * The inputs still carry a native `placeholder` that matches the
 * indicator text so axe-ee contrast rules cover the grey rendering in
 * one axe sweep.
 */

interface ExecutionPanelProps {
  agentId: string;
  parentUnitId: string | null;
}

const FIELD_UNSET = "__unset__";

type ExecutionField =
  | "image"
  | "runtime"
  | "tool"
  | "provider"
  | "model"
  | "hosting";

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

  const persisted = agentExecutionQuery.data ?? null;
  const unitDefaults: UnitExecutionResponse | null =
    unitExecutionQuery.data ?? null;

  const [form, setForm] = useState<AgentExecutionResponse>({});
  const [seededFor, setSeededFor] = useState<string | null>(null);
  const fingerprint = useMemo(
    () => JSON.stringify(persisted ?? null),
    [persisted],
  );
  if (fingerprint !== seededFor) {
    setForm(persisted ?? {});
    setSeededFor(fingerprint);
  }

  const setField = <K extends keyof AgentExecutionResponse>(
    key: K,
    value: AgentExecutionResponse[K],
  ) => {
    setForm((prev) => ({ ...prev, [key]: value }));
  };

  const dirty = useMemo(() => {
    const current = persisted ?? {};
    return (
      (form.image ?? null) !== (current.image ?? null) ||
      (form.runtime ?? null) !== (current.runtime ?? null) ||
      (form.tool ?? null) !== (current.tool ?? null) ||
      (form.provider ?? null) !== (current.provider ?? null) ||
      (form.model ?? null) !== (current.model ?? null) ||
      (form.hosting ?? null) !== (current.hosting ?? null)
    );
  }, [form, persisted]);

  // Resolve the effective `tool` for gating: agent's own value wins,
  // unit default fills in otherwise.
  const effectiveToolForGating =
    form.tool ?? persisted?.tool ?? unitDefaults?.tool ?? null;
  const showProvider =
    effectiveToolForGating === null || effectiveToolForGating === "dapr-agent";

  // #641: tools that hide Provider (claude-code / codex / gemini) still
  // expose a Model dropdown populated from that tool's catalog. Derive
  // the runtime id from the effective tool; use the explicit Provider
  // value when dapr-agent is active. `custom` and unset tool for a
  // non-dapr-agent effective return null, which collapses the Model
  // dropdown into the inherited/free-text fallback below. #735: route
  // the catalog through `useAgentRuntimeModels` so the hardcoded
  // provider→model table is gone — the tenant's installed runtimes
  // are the single source of truth.
  const toolForCatalog = (effectiveToolForGating ?? null) as
    | ExecutionTool
    | null;
  const toolRuntimeId =
    toolForCatalog !== null ? getToolRuntimeId(toolForCatalog) : null;
  const runtimeIdForModels = showProvider
    ? providerToRuntimeId(
        form.provider ??
          persisted?.provider ??
          unitDefaults?.provider ??
          "",
      )
    : toolRuntimeId;
  const showModel = showProvider || toolRuntimeId !== null;
  const agentRuntimeModelsQuery = useAgentRuntimeModels(
    runtimeIdForModels ?? "",
    { enabled: runtimeIdForModels !== null },
  );
  const providerModelsEnabled = runtimeIdForModels !== null;
  const providerModels =
    agentRuntimeModelsQuery.data?.map((m) => m.id) ?? null;

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
        description: err instanceof Error ? err.message : String(err),
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
        description: err instanceof Error ? err.message : String(err),
        variant: "destructive",
      });
    },
  });

  const clearField = (field: ExecutionField) => {
    const next: AgentExecutionResponse = {
      ...(persisted ?? {}),
      [field]: null,
    };
    setForm(next);
    setMutation.mutate(next);
  };

  const handleSave = () => {
    const next: AgentExecutionResponse = {
      image: form.image ?? null,
      runtime: form.runtime ?? null,
      tool: form.tool ?? null,
      // #641: Provider stays gated on dapr-agent (Option A for #598);
      // Model rides along whenever the tool has a known catalog.
      provider: showProvider ? (form.provider ?? null) : null,
      model: showModel ? (form.model ?? null) : null,
      hosting: form.hosting ?? null,
    };
    setMutation.mutate(next);
  };

  if (agentExecutionQuery.isPending) {
    return <Skeleton className="h-64" />;
  }

  const hasAny = persisted !== null && !isEmpty(persisted);

  // Inherited-value helpers. Each returns the owning unit's value for
  // the slot when the agent leaves the slot blank — used to seed a
  // native `placeholder` AND the accessible help copy so the source
  // stays visible to screen readers.
  const inherited = (slot: keyof UnitExecutionResponse): string | null => {
    const agentValue = form[slot as keyof AgentExecutionResponse];
    if (agentValue) return null;
    return unitDefaults?.[slot] ?? null;
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
          Agent-level overrides for the container runtime and launcher.
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
          <Input
            value={form.image ?? ""}
            onChange={(e) =>
              setField("image", e.target.value ? e.target.value : null)
            }
            placeholder={
              inherited("image")
                ? `inherited from unit: ${inherited("image")}`
                : "ghcr.io/... or localhost/spring-voyage-agent-claude-code:latest"
            }
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
          label="Runtime"
          help={
            inherited("runtime")
              ? `inherited from unit: ${inherited("runtime")}`
              : "Container runtime the launcher drives."
          }
          onClear={persisted?.runtime ? () => clearField("runtime") : undefined}
          busy={setMutation.isPending}
        >
          <SelectField
            value={form.runtime ?? null}
            onChange={(next) => setField("runtime", next)}
            options={EXECUTION_RUNTIMES}
            inheritedLabel={inherited("runtime")}
            ariaLabel="Agent execution runtime"
            testid="agent-execution-runtime-select"
          />
        </FieldRow>

        <FieldRow
          label="Tool"
          help={
            inherited("tool")
              ? `inherited from unit: ${inherited("tool")}`
              : "Launcher key the dispatcher uses."
          }
          onClear={persisted?.tool ? () => clearField("tool") : undefined}
          busy={setMutation.isPending}
        >
          <SelectField
            value={form.tool ?? null}
            onChange={(next) => setField("tool", next)}
            options={EXECUTION_TOOL_KEYS}
            inheritedLabel={inherited("tool")}
            ariaLabel="Agent execution tool"
            testid="agent-execution-tool-select"
          />
        </FieldRow>

        {showProvider && (
          <FieldRow
            label="Provider"
            help={
              inherited("provider")
                ? `inherited from unit: ${inherited("provider")}`
                : "LLM provider — only meaningful when Tool is Dapr Agent."
            }
            onClear={
              persisted?.provider ? () => clearField("provider") : undefined
            }
            busy={setMutation.isPending}
          >
            <SelectField
              value={form.provider ?? null}
              onChange={(next) => setField("provider", next)}
              options={EXECUTION_PROVIDERS}
              inheritedLabel={inherited("provider")}
              ariaLabel="Agent execution provider"
              testid="agent-execution-provider-select"
            />
          </FieldRow>
        )}

        {showModel && (
          <FieldRow
            label="Model"
            help={
              inherited("model")
                ? `inherited from unit: ${inherited("model")}`
                : "Model identifier."
            }
            onClear={persisted?.model ? () => clearField("model") : undefined}
            busy={setMutation.isPending}
          >
            {providerModelsEnabled &&
            providerModels &&
            providerModels.length > 0 ? (
              <select
                value={form.model ?? ""}
                onChange={(e) =>
                  setField("model", e.target.value ? e.target.value : null)
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
                value={form.model ?? ""}
                onChange={(e) =>
                  setField("model", e.target.value ? e.target.value : null)
                }
                placeholder={
                  inherited("model")
                    ? `inherited from unit: ${inherited("model")}`
                    : "e.g. claude-sonnet-4-6"
                }
                aria-label="Agent execution model"
                data-testid="agent-execution-model-input"
                className={
                  !form.model && inherited("model")
                    ? "italic text-muted-foreground placeholder:italic placeholder:text-muted-foreground"
                    : undefined
                }
              />
            )}
          </FieldRow>
        )}

        {showProvider &&
          (form.provider ?? persisted?.provider ?? unitDefaults?.provider) && (
            <CredentialStatusBanner
              providerId={
                (form.provider ??
                  persisted?.provider ??
                  unitDefaults?.provider) as string
              }
            />
          )}

        {/* Hosting — agent-exclusive. Unit defaults don't carry a
            hosting slot so there's nothing to inherit from. */}
        <FieldRow
          label="Hosting"
          help="Agent lifecycle — ephemeral launches per-message; persistent runs continuously."
          onClear={persisted?.hosting ? () => clearField("hosting") : undefined}
          busy={setMutation.isPending}
        >
          <SelectField
            value={form.hosting ?? null}
            onChange={(next) => setField("hosting", next)}
            options={EXECUTION_HOSTING_MODES}
            inheritedLabel={null}
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
            data-testid={`agent-execution-clear-${label.toLowerCase()}`}
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

function isEmpty(block: AgentExecutionResponse): boolean {
  return (
    !block.image &&
    !block.runtime &&
    !block.tool &&
    !block.provider &&
    !block.model &&
    !block.hosting
  );
}

/**
 * Credential-status banner — identical palette to the wizard Step 1
 * and the unit Execution tab so the three surfaces share one axe
 * sweep.
 */
function CredentialStatusBanner({ providerId }: { providerId: string }) {
  const { data, isPending, isError } = useProviderCredentialStatus(providerId);

  if (isPending) return null;

  if (isError || !data) {
    return (
      <p className="text-xs text-muted-foreground" role="status">
        Could not verify {providerLabel(providerId)} credentials.
      </p>
    );
  }

  const displayName = providerLabel(providerId);

  if (data.resolvable) {
    const originHint =
      data.source === "unit"
        ? `${displayName} credentials: set on unit`
        : data.source === "tenant"
          ? `${displayName} credentials: inherited from tenant default`
          : `${displayName} reachable`;
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
            {displayName} credentials: not configured.{" "}
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
