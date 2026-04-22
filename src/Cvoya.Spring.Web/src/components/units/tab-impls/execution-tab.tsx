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
  useAgentRuntimeModels,
  useProviderCredentialStatus,
  useUnitExecution,
} from "@/lib/api/queries";
import { queryKeys } from "@/lib/api/query-keys";
import type { UnitExecutionResponse } from "@/lib/api/types";
import {
  EXECUTION_PROVIDERS,
  EXECUTION_RUNTIMES,
  EXECUTION_TOOL_KEYS,
} from "@/lib/api/types";
import { getToolRuntimeId, type ExecutionTool } from "@/lib/ai-models";

/**
 * #735: collapse the canonical provider string space
 * (`anthropic`/`openai`/`google`/`ollama`) onto the runtime id space the
 * agent-runtimes endpoint keys on. Anthropic's runtime id is `claude`;
 * everything else round-trips verbatim. Returns `null` when the provider
 * isn't a known runtime — the caller renders a free-text Model input in
 * that case.
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
 * Unit Execution tab (#601 / #603 / #409 B-wide, portal half).
 *
 * Exposes the unit-level defaults (image / runtime / tool / provider /
 * model) that member agents inherit at dispatch time. Reads / writes
 * through `/api/v1/units/{id}/execution` (backend landed in PR #628);
 * each field is independently editable and independently clearable so
 * the operator can declare `runtime: podman` only and leave `image`
 * etc. for each agent to provide.
 *
 * Gating mirrors #598 (PR #627) + #641 (PR #645): Provider only
 * renders when the declared launcher tool is `dapr-agent` or unset —
 * other launchers hard-code their provider inside their own CLI so a
 * Provider dropdown would be misleading. Model is rendered whenever
 * the effective tool has a known model catalog (dapr-agent via the
 * selected provider; claude-code / codex / gemini via their implicit
 * provider). `custom` collapses the Model slot entirely. The
 * credential-status banner from #598 reuses its
 * `useProviderCredentialStatus` hook whenever Provider is shown and
 * has a selected value.
 *
 * Follow-ups the scope deliberately defers:
 *   - Image reference autocomplete → #622 (V2.1).
 *   - Registry discovery → #623 (V2.1).
 */

interface ExecutionTabProps {
  unitId: string;
}

const FIELD_UNSET = "__unset__";

export function ExecutionTab({ unitId }: ExecutionTabProps) {
  const { toast } = useToast();
  const queryClient = useQueryClient();
  const executionQuery = useUnitExecution(unitId);

  const persisted = executionQuery.data ?? null;

  // Local draft — seeded from the server payload once, then re-seeded
  // whenever the server identity changes (keyed remount below).
  const [form, setForm] = useState<UnitExecutionResponse>({});
  const [seededFor, setSeededFor] = useState<string | null>(null);
  const fingerprint = useMemo(
    () => JSON.stringify(persisted ?? null),
    [persisted],
  );
  if (fingerprint !== seededFor) {
    setForm(persisted ?? {});
    setSeededFor(fingerprint);
  }

  const setField = <K extends keyof UnitExecutionResponse>(
    key: K,
    value: UnitExecutionResponse[K],
  ) => {
    setForm((prev) => ({ ...prev, [key]: value }));
  };

  // Per-field dirtiness — only the fields the operator actually touched
  // differ from the persisted block.
  const dirty = useMemo(() => {
    const current = persisted ?? {};
    return (
      (form.image ?? null) !== (current.image ?? null) ||
      (form.runtime ?? null) !== (current.runtime ?? null) ||
      (form.tool ?? null) !== (current.tool ?? null) ||
      (form.provider ?? null) !== (current.provider ?? null) ||
      (form.model ?? null) !== (current.model ?? null)
    );
  }, [form, persisted]);

  const effectiveToolForGating = form.tool ?? null;
  const showProvider =
    effectiveToolForGating === null || effectiveToolForGating === "dapr-agent";

  // #641: tools that hide Provider (claude-code / codex / gemini) still
  // expose a Model dropdown populated from that tool's catalog. Derive
  // the runtime id from the effective tool; use the explicit Provider
  // value when dapr-agent is active. `custom` returns null, which
  // collapses the Model slot entirely. #735: route the catalog through
  // `useAgentRuntimeModels` so the hardcoded provider→model table is
  // gone — the tenant's installed runtimes are the single source of
  // truth.
  const toolForCatalog = effectiveToolForGating as ExecutionTool | null;
  const toolRuntimeId =
    toolForCatalog !== null ? getToolRuntimeId(toolForCatalog) : null;
  const runtimeIdForModels = showProvider
    ? providerToRuntimeId(form.provider ?? "")
    : toolRuntimeId;
  const showModel = showProvider || toolRuntimeId !== null;

  // Provider-dependent model suggestions (#597 / PR #613). The field is
  // a plain text input when no provider is selected, falling back to a
  // dropdown when we have a known set.
  const providerModelsEnabled = runtimeIdForModels !== null;
  const agentRuntimeModelsQuery = useAgentRuntimeModels(
    runtimeIdForModels ?? "",
    { enabled: providerModelsEnabled },
  );
  const providerModels =
    agentRuntimeModelsQuery.data?.map((m) => m.id) ?? null;

  const setMutation = useMutation({
    mutationFn: async (
      next: UnitExecutionResponse,
    ): Promise<UnitExecutionResponse> => {
      // A fully empty PUT hits the 400; fall through to DELETE so the
      // "clear everything" intent is covered by the dedicated verb.
      if (isEmpty(next)) {
        await api.clearUnitExecution(unitId);
        return {};
      }
      return await api.setUnitExecution(unitId, next);
    },
    onSuccess: (updated) => {
      queryClient.setQueryData(queryKeys.units.execution(unitId), updated);
      toast({ title: "Execution defaults saved" });
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
    mutationFn: async (): Promise<UnitExecutionResponse> => {
      await api.clearUnitExecution(unitId);
      return {};
    },
    onSuccess: (cleared) => {
      queryClient.setQueryData(queryKeys.units.execution(unitId), cleared);
      toast({ title: "Execution defaults cleared" });
    },
    onError: (err) => {
      toast({
        title: "Clear failed",
        description: err instanceof Error ? err.message : String(err),
        variant: "destructive",
      });
    },
  });

  // Per-field clear: re-PUT with the remaining fields, or DELETE when
  // the residual block is empty. Matches PR #628's partial-update
  // contract — the scope doc names this explicitly.
  const clearField = (field: keyof UnitExecutionResponse) => {
    const next: UnitExecutionResponse = { ...(persisted ?? {}), [field]: null };
    setForm(next);
    setMutation.mutate(next);
  };

  const handleSave = () => {
    // Null out stale gated fields so the wire shape stays clean — if
    // the operator switched Tool away from dapr-agent we shouldn't keep
    // the prior provider value around. #641: Provider stays gated on
    // dapr-agent (Option A for #598); Model rides along whenever the
    // tool has a known catalog.
    const next: UnitExecutionResponse = {
      image: form.image ?? null,
      runtime: form.runtime ?? null,
      tool: form.tool ?? null,
      provider: showProvider ? (form.provider ?? null) : null,
      model: showModel ? (form.model ?? null) : null,
    };
    setMutation.mutate(next);
  };

  if (executionQuery.isPending) {
    return (
      <div className="space-y-4">
        <Skeleton className="h-32" />
        <Skeleton className="h-48" />
      </div>
    );
  }

  const hasAny = persisted !== null && !isEmpty(persisted);

  return (
    <div className="space-y-4" data-testid="execution-tab">
      <Card data-testid="unit-execution-card">
        <CardHeader className="flex flex-row items-center justify-between gap-2 space-y-0 pb-2">
          <CardTitle className="flex items-center gap-2 text-base">
            <Container className="h-4 w-4" />
            <span>Execution defaults</span>
            {hasAny ? (
              <Badge variant="default" className="ml-2 text-xs font-normal">
                Configured
              </Badge>
            ) : (
              <Badge variant="outline" className="ml-2 text-xs font-normal">
                Unset
              </Badge>
            )}
          </CardTitle>
          {hasAny && (
            <Button
              size="sm"
              variant="outline"
              onClick={() => clearAllMutation.mutate()}
              disabled={clearAllMutation.isPending}
              aria-label="Clear execution defaults"
            >
              Clear all
            </Button>
          )}
        </CardHeader>
        <CardContent className="space-y-4 text-sm">
          <p className="text-xs text-muted-foreground">
            Unit-level defaults for the container runtime and launcher that
            member agents inherit at dispatch time. Every field is
            independently optional — declare only what you want enforced
            here; agents can override any value on their own Execution
            panel. Round-trips the same shape as{" "}
            <code>spring unit execution set</code>.
          </p>

          {/* Image — plain text input (Shape 1). Autocomplete / registry
              discovery are tracked follow-ups (#622, #623). */}
          <FieldRow
            label="Image"
            help="Default container image used to launch member agents. Individual agents can override this on their Execution panel."
            onClear={
              persisted?.image ? () => clearField("image") : undefined
            }
            busy={setMutation.isPending}
          >
            <Input
              value={form.image ?? ""}
              onChange={(e) =>
                setField("image", e.target.value ? e.target.value : null)
              }
              placeholder="ghcr.io/... or localhost/spring-voyage-agent:latest"
              aria-label="Execution image"
              data-testid="execution-image-input"
            />
          </FieldRow>

          {/* Runtime — fixed dropdown. */}
          <FieldRow
            label="Runtime"
            help="Container runtime the launcher drives."
            onClear={
              persisted?.runtime ? () => clearField("runtime") : undefined
            }
            busy={setMutation.isPending}
          >
            <SelectField
              value={form.runtime ?? null}
              onChange={(next) => setField("runtime", next)}
              options={EXECUTION_RUNTIMES}
              unsetLabel="(leave to default)"
              ariaLabel="Execution runtime"
              testid="execution-runtime-select"
            />
          </FieldRow>

          {/* Tool — launcher key. */}
          <FieldRow
            label="Tool"
            help="Launcher key the dispatcher uses to bring the agent container up."
            onClear={persisted?.tool ? () => clearField("tool") : undefined}
            busy={setMutation.isPending}
          >
            <SelectField
              value={form.tool ?? null}
              onChange={(next) => setField("tool", next)}
              options={EXECUTION_TOOL_KEYS}
              unsetLabel="(leave to default)"
              ariaLabel="Execution tool"
              testid="execution-tool-select"
            />
          </FieldRow>

          {/* Provider — gated behind tool=dapr-agent (#598 Option A). */}
          {showProvider && (
            <FieldRow
              label="Provider"
              help="LLM provider — only meaningful when Tool is Dapr Agent."
              onClear={
                persisted?.provider
                  ? () => clearField("provider")
                  : undefined
              }
              busy={setMutation.isPending}
            >
              <SelectField
                value={form.provider ?? null}
                onChange={(next) => setField("provider", next)}
                options={EXECUTION_PROVIDERS}
                unsetLabel="(leave to default)"
                ariaLabel="Execution provider"
                testid="execution-provider-select"
              />
            </FieldRow>
          )}

          {/* Model — #641: rendered whenever the tool has a known
              catalog, which includes dapr-agent (via selected Provider)
              and claude-code / codex / gemini (via the tool's implicit
              provider). `custom` and unset tool without a selected
              Provider collapse the field. */}
          {showModel && (
            <FieldRow
              label="Model"
              help="Model identifier. Populated from the provider's live catalog when available."
              onClear={
                persisted?.model ? () => clearField("model") : undefined
              }
              busy={setMutation.isPending}
            >
              {providerModelsEnabled &&
              providerModels &&
              providerModels.length > 0 ? (
                <select
                  value={form.model ?? ""}
                  onChange={(e) =>
                    setField(
                      "model",
                      e.target.value ? e.target.value : null,
                    )
                  }
                  aria-label="Execution model"
                  data-testid="execution-model-select"
                  className="flex h-9 w-full rounded-md border border-input bg-background px-3 text-sm shadow-sm focus-visible:outline-none focus-visible:ring-1 focus-visible:ring-ring"
                >
                  <option value="">(leave to default)</option>
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
                    setField(
                      "model",
                      e.target.value ? e.target.value : null,
                    )
                  }
                  placeholder="e.g. claude-sonnet-4-6"
                  aria-label="Execution model"
                  data-testid="execution-model-input"
                />
              )}
            </FieldRow>
          )}

          {showProvider && form.provider && (
            <CredentialStatusBanner providerId={form.provider} />
          )}

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
    </div>
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
            aria-label={`Clear ${label.toLowerCase()}`}
            data-testid={`execution-clear-${label.toLowerCase()}`}
          >
            <Trash2 className="mr-1 h-3 w-3" />
            Clear
          </Button>
        )}
      </div>
      {children}
      <p className="text-xs text-muted-foreground">{help}</p>
    </div>
  );
}

interface SelectFieldProps {
  value: string | null;
  onChange: (next: string | null) => void;
  options: readonly string[];
  unsetLabel: string;
  ariaLabel: string;
  testid: string;
}

function SelectField({
  value,
  onChange,
  options,
  unsetLabel,
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
      className="flex h-9 w-full rounded-md border border-input bg-background px-3 text-sm shadow-sm focus-visible:outline-none focus-visible:ring-1 focus-visible:ring-ring"
    >
      <option value={FIELD_UNSET}>{unsetLabel}</option>
      {options.map((opt) => (
        <option key={opt} value={opt}>
          {opt}
        </option>
      ))}
    </select>
  );
}

function isEmpty(block: UnitExecutionResponse): boolean {
  return (
    !block.image && !block.runtime && !block.tool && !block.provider && !block.model
  );
}

/**
 * Inline credential-status banner — the same pattern PR #627 added on
 * the unit-create wizard Step 1. Reused here so the Execution tab
 * surfaces "provider not configured" at edit time rather than at
 * dispatch. Mirrors DESIGN.md §7.4a (warning/success alert palette).
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
        data-testid="execution-credential-status"
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
      data-testid="execution-credential-status"
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
