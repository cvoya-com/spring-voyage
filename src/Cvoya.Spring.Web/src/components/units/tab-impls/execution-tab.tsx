"use client";

import { useMemo, useState } from "react";
import Link from "next/link";

import { toExplorerPathSegment } from "@/lib/explorer-url";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import {
  AlertTriangle,
  Bot,
  CheckCircle2,
  Container,
  Settings2,
  Trash2,
} from "lucide-react";

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
  useModelProviderModels,
  useModelProviders,
  useProviderCredentialStatus,
  useUnitExecution,
} from "@/lib/api/queries";
import { queryKeys } from "@/lib/api/query-keys";
import { formatTranslatedError } from "@/lib/api/translate-error";
import type {
  UnitExecutionResponse,
  UnitMembershipResponse,
} from "@/lib/api/types";
import {
  DEFAULT_HOSTING_MODE,
  HOSTING_MODES,
  RUNTIME_LIST,
  getAllowedProviders,
  getFixedProvider,
  isRuntimeProviderFixed,
  type HostingMode,
} from "@/lib/ai-models";
import { useImageHistory } from "@/lib/image-history";
import {
  providerDisplayName,
  runtimeCredentialDescriptor,
  type RuntimeCredentialDescriptor,
} from "@/lib/runtime-credentials";

/**
 * Unit Execution tab (ADR-0038).
 *
 * Exposes the unit-level defaults (image / agent runtime / model
 * provider / model) that member agents inherit at dispatch time. Reads
 * / writes through `/api/v1/units/{id}/execution`; each field is
 * independently editable and independently clearable.
 *
 * ADR-0038 §1: the provider picker is **conditional**.
 *   - When the chosen runtime is fixed-provider (`claude-code` /
 *     `codex` / `gemini`), the picker is hidden and the model
 *     dropdown is filtered to that runtime's single allowed provider.
 *   - When the runtime is multi-provider (`spring-voyage`, future
 *     `custom`), the picker is shown as a model-list filter against
 *     the tenant's installed model providers.
 *
 * Local form state mirrors the wire shape directly — `{image, runtime,
 * model: {provider, id}}` — so the on-save handler can hand the form
 * to the API without an intermediate projection.
 */

interface ExecutionTabProps {
  unitId: string;
}

const FIELD_UNSET = "__unset__";

type ModelDraft = {
  provider: string | null;
  id: string | null;
};

type UnitExecutionDraft = Omit<UnitExecutionResponse, "model"> & {
  model: ModelDraft | null;
};

type MemberHostingRow = {
  agentAddress: string;
  displayName: string;
  hosting: HostingMode;
  declared: boolean;
  href: string;
};

const HOSTING_LABELS = new Map<HostingMode, string>(
  HOSTING_MODES.map((mode) => [mode.id, mode.label]),
);

function isEmpty(block: UnitExecutionResponse): boolean {
  return !block.image && !block.runtime && !block.model;
}

function isHostingMode(value: string | null | undefined): value is HostingMode {
  return value === "ephemeral" || value === "persistent";
}

function normalizeHostingMode(value: string | null | undefined): HostingMode {
  return isHostingMode(value) ? value : DEFAULT_HOSTING_MODE;
}

function hostingLabel(mode: HostingMode): string {
  return HOSTING_LABELS.get(mode) ?? mode;
}

function memberHostingRow(
  membership: UnitMembershipResponse,
): MemberHostingRow {
  // PR-#2223 follow-up: hosting is now projected onto the membership row
  // server-side (M lookups instead of N) so the unit Execution tab no
  // longer needs the full-tenant /agents fan-out to render this card.
  const declaredHosting = membership.agentHostingMode ?? null;
  return {
    agentAddress: membership.agentAddress,
    displayName:
      membership.agentDisplayName || membership.agentAddress,
    hosting: normalizeHostingMode(declaredHosting),
    declared: isHostingMode(declaredHosting),
    href: `/explorer/units/${encodeURIComponent(toExplorerPathSegment(membership.agentAddress))}?tab=Config`,
  };
}

async function loadMemberHosting(unitId: string): Promise<MemberHostingRow[]> {
  const memberships = await api.listUnitMemberships(unitId);
  return memberships.map(memberHostingRow);
}

function persistedToForm(
  persisted: UnitExecutionResponse | null,
): UnitExecutionDraft {
  return {
    image: persisted?.image ?? null,
    runtime: persisted?.runtime ?? null,
    model: persisted?.model
      ? {
          provider: persisted.model.provider,
          id: persisted.model.id,
        }
      : null,
  };
}

function toPayload(form: UnitExecutionDraft): UnitExecutionResponse {
  return {
    image: form.image ?? null,
    runtime: form.runtime ?? null,
    model:
      form.model?.provider && form.model.id
        ? {
            provider: form.model.provider,
            id: form.model.id,
          }
        : null,
  };
}

export function ExecutionTab({ unitId }: ExecutionTabProps) {
  const { toast } = useToast();
  const queryClient = useQueryClient();
  const executionQuery = useUnitExecution(unitId);
  const installedProvidersQuery = useModelProviders();
  const installedProviders = useMemo(
    () => installedProvidersQuery.data ?? [],
    [installedProvidersQuery.data],
  );
  const memberHostingQuery = useQuery({
    queryKey: queryKeys.units.memberHosting(unitId),
    queryFn: () => loadMemberHosting(unitId),
    enabled: Boolean(unitId),
  });

  const persisted = executionQuery.data ?? null;

  // Local draft keeps a provider-only selection so the provider picker
  // can act as a model-catalogue filter. Saves still go through
  // `toPayload`, which only emits complete `{ provider, id }` models.
  const [form, setForm] = useState<UnitExecutionDraft>(() =>
    persistedToForm(null),
  );
  const [seededFor, setSeededFor] = useState<string | null>(null);
  const imageHistory = useImageHistory();
  const fingerprint = useMemo(
    () => JSON.stringify(persisted ?? null),
    [persisted],
  );
  if (fingerprint !== seededFor) {
    setForm(persistedToForm(persisted));
    setSeededFor(fingerprint);
  }

  const dirty = useMemo(() => {
    const current = toPayload(persistedToForm(persisted));
    const next = toPayload(form);
    return (
      next.image !== current.image ||
      next.runtime !== current.runtime ||
      (next.model?.provider ?? null) !== (current.model?.provider ?? null) ||
      (next.model?.id ?? null) !== (current.model?.id ?? null)
    );
  }, [form, persisted]);

  const runtimeId = form.runtime ?? null;
  const providerFixed = isRuntimeProviderFixed(runtimeId);
  const fixedProvider = getFixedProvider(runtimeId);
  const allowedProviders = getAllowedProviders(runtimeId);

  // Compute the effective provider that drives the model dropdown.
  // Fixed-provider runtimes pin it; multi-provider runtimes use the
  // operator's pick from the picker.
  const effectiveProvider = providerFixed
    ? fixedProvider
    : (form.model?.provider ?? null);
  const credentialDescriptor = useMemo(
    () => runtimeCredentialDescriptor(runtimeId, effectiveProvider),
    [runtimeId, effectiveProvider],
  );

  // Model dropdown source: the per-provider catalogue.
  const modelsQuery = useModelProviderModels(effectiveProvider ?? "", {
    enabled: effectiveProvider !== null,
  });
  const providerModels = modelsQuery.data?.map((m) => m.id) ?? null;

  // Provider picker options (multi-provider runtimes only): the
  // intersection of the runtime's allow-list with the tenant's
  // installed providers.
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
      next: UnitExecutionResponse,
    ): Promise<UnitExecutionResponse> => {
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
        description: formatTranslatedError(err),
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
        description: formatTranslatedError(err),
        variant: "destructive",
      });
    },
  });

  // Mutate the model object as a draft unit. The UI may hold provider
  // without id while it loads a catalogue; `toPayload` strips partial
  // model drafts before save.
  const setModelField = (key: "provider" | "id", value: string | null) => {
    setForm((prev) => {
      const previousProvider = prev.model?.provider ?? null;
      const nextProvider =
        key === "provider" ? value : (previousProvider ?? effectiveProvider);
      const nextId =
        key === "id"
          ? value
          : value && value === previousProvider
            ? (prev.model?.id ?? null)
            : null;
      const model =
        nextProvider || nextId
          ? { provider: nextProvider, id: nextId }
          : null;
      return { ...prev, model };
    });
  };

  // Switching runtime must reset the model — its provider domain
  // changed (claude-code can only host anthropic models, etc.).
  const setRuntime = (next: string | null) => {
    setForm((prev) => {
      if (next === null) return { ...prev, runtime: null, model: null };
      const fixed = getFixedProvider(next);
      // Fixed-provider runtimes seed the provider eagerly so the
      // model dropdown filters on it without a second click.
      const nextProvider = fixed ?? null;
      // Reset model id whenever the provider changes.
      const prevProvider = prev.model?.provider ?? null;
      const keepModelId =
        prevProvider !== null && prevProvider === nextProvider
          ? (prev.model?.id ?? null)
          : null;
      const model =
        nextProvider || keepModelId
          ? { provider: nextProvider, id: keepModelId }
          : null;
      return { ...prev, runtime: next, model };
    });
  };

  const clearField = (
    field: "image" | "runtime" | "providerOnly" | "model",
  ) => {
    let next = form;
    if (field === "image") next = { ...form, image: null };
    if (field === "runtime") next = { ...form, runtime: null, model: null };
    if (field === "providerOnly") next = { ...form, model: null };
    if (field === "model") {
      next = {
        ...form,
        model: form.model?.provider
          ? { provider: form.model.provider, id: null }
          : null,
      };
    }
    setForm(next);
    setMutation.mutate(toPayload(next));
  };

  const handleSave = () => {
    setMutation.mutate(toPayload(form));
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
            Unit-level defaults for the agent image, runtime, and model
            that member agents inherit at dispatch time. Every field is
            independently optional — declare only what you want enforced
            here; agents can override any value on their own Execution
            panel. Round-trips the same shape as{" "}
            <code>spring unit execution set</code>.
          </p>

          {/* Image — text input with built-in image suggestions (#622). */}
          <FieldRow
            label="Image"
            help="Default container image used to launch member agents. Individual agents can override this on their Execution panel."
            onClear={
              persisted?.image ? () => clearField("image") : undefined
            }
            busy={setMutation.isPending}
          >
            <datalist id="unit-execution-image-suggestions">
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
              placeholder="ghcr.io/cvoya-com/spring-voyage-claude-code-base:latest"
              list={imageHistory.length > 0 ? "unit-execution-image-suggestions" : undefined}
              aria-label="Execution image"
              data-testid="execution-image-input"
            />
          </FieldRow>

          {/* Agent Runtime — launcher key. ADR-0038 renamed the wire
              field `agent` → `runtime`. */}
          <FieldRow
            label="Agent Runtime"
            help="Agent runtime the dispatcher uses to bring the agent container up."
            onClear={persisted?.runtime ? () => clearField("runtime") : undefined}
            busy={setMutation.isPending}
          >
            <RuntimeSelect
              value={form.runtime ?? null}
              onChange={setRuntime}
              ariaLabel="Agent runtime"
              testid="execution-agent-runtime-select"
            />
          </FieldRow>

          {/* Model Provider — conditional. Hidden when the runtime
              fixes its provider (claude-code / codex / gemini); shown
              as a model-list filter when the runtime is multi-provider
              (spring-voyage). The `custom` slot is reserved for v0.2
              and ships with an empty allow-list — the picker stays
              hidden until that lands. */}
          {!providerFixed &&
            runtimeId !== null &&
            runtimeId !== "custom" && (
            <FieldRow
              label="Model Provider"
              help={
                providerOptions.length === 0
                  ? "No installed model providers match this runtime. Install one with `spring model-provider install <id>`."
                  : "LLM provider that hosts the model the runtime dispatches against."
              }
              onClear={
                form.model?.provider
                  ? () => clearField("providerOnly")
                  : undefined
              }
              busy={setMutation.isPending}
            >
              <SelectField
                value={form.model?.provider ?? null}
                onChange={(next) => setModelField("provider", next)}
                options={providerOptions}
                unsetLabel="(leave to default)"
                ariaLabel="Model provider"
                testid="execution-provider-select"
              />
            </FieldRow>
          )}

          {/* Model — always rendered. Populated from the effective
              provider's catalogue. */}
          <FieldRow
            label="Model"
            help={
              effectiveProvider === null
                ? "Pick an Agent Runtime (and Model Provider, if multi-provider) to load the model catalogue."
                : "Model identifier within the selected provider's catalogue."
            }
            onClear={
              form.model?.id ? () => clearField("model") : undefined
            }
            busy={setMutation.isPending}
          >
            {effectiveProvider !== null &&
            providerModels &&
            providerModels.length > 0 ? (
              <select
                value={form.model?.id ?? ""}
                onChange={(e) =>
                  setModelField(
                    "id",
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
                value={form.model?.id ?? ""}
                onChange={(e) =>
                  setModelField(
                    "id",
                    e.target.value ? e.target.value : null,
                  )
                }
                placeholder="e.g. claude-sonnet-4-6"
                aria-label="Execution model"
                data-testid="execution-model-input"
                disabled={effectiveProvider === null}
              />
            )}
          </FieldRow>

          {effectiveProvider !== null && (
            <CredentialStatusBanner
              providerId={effectiveProvider}
              credential={credentialDescriptor}
            />
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

      <MemberHostingCard
        rows={memberHostingQuery.data ?? []}
        isPending={memberHostingQuery.isPending}
        isError={memberHostingQuery.isError}
      />
    </div>
  );
}

interface MemberHostingCardProps {
  rows: readonly MemberHostingRow[];
  isPending: boolean;
  isError: boolean;
}

function MemberHostingCard({
  rows,
  isPending,
  isError,
}: MemberHostingCardProps) {
  const persistentCount = rows.filter(
    (row) => row.hosting === "persistent",
  ).length;
  const ephemeralCount = rows.filter(
    (row) => row.hosting === "ephemeral",
  ).length;

  return (
    <Card data-testid="unit-member-hosting-card">
      <CardHeader className="space-y-0 pb-2">
        <CardTitle className="flex flex-wrap items-center gap-2 text-base">
          <Bot className="h-4 w-4" />
          <span>Member agent hosting</span>
          {!isPending && !isError && rows.length > 0 ? (
            <span
              className="flex flex-wrap items-center gap-1"
              data-testid="unit-member-hosting-summary"
            >
              <Badge variant="success" className="text-xs font-normal">
                {persistentCount} persistent
              </Badge>
              <Badge variant="outline" className="text-xs font-normal">
                {ephemeralCount} ephemeral
              </Badge>
            </span>
          ) : null}
        </CardTitle>
      </CardHeader>
      <CardContent className="space-y-3 text-sm">
        <p className="text-xs text-muted-foreground">
          Hosting is owned by each member agent. This unit view shows the
          current value and links to the agent Config tab for edits.
        </p>

        {isPending ? (
          <p className="text-sm text-muted-foreground">
            Loading member hosting…
          </p>
        ) : isError ? (
          <p className="text-sm text-destructive" role="alert">
            Could not load member agent hosting.
          </p>
        ) : rows.length === 0 ? (
          <p className="text-sm text-muted-foreground">
            No member agents are assigned to this unit.
          </p>
        ) : (
          <ul className="divide-y divide-border rounded-md border border-border">
            {rows.map((row) => (
              <li
                key={row.agentAddress}
                data-testid={`unit-member-hosting-${row.agentAddress}`}
                className="flex flex-wrap items-center gap-3 px-3 py-2"
              >
                <div className="min-w-0 flex-1">
                  <p className="truncate font-medium">{row.displayName}</p>
                  <p className="truncate font-mono text-[11px] text-muted-foreground">
                    {row.agentAddress}
                  </p>
                </div>
                <div className="flex flex-wrap items-center gap-1.5">
                  <Badge
                    variant={
                      row.hosting === "persistent" ? "success" : "outline"
                    }
                    className="capitalize"
                  >
                    {hostingLabel(row.hosting)}
                  </Badge>
                  {!row.declared ? (
                    <Badge variant="outline" className="font-normal">
                      Default
                    </Badge>
                  ) : null}
                </div>
                <Link
                  href={row.href}
                  aria-label={`Edit hosting for ${row.displayName}`}
                  className="inline-flex h-8 items-center gap-1 rounded-md border border-input px-2 text-xs font-medium hover:bg-accent focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring"
                >
                  <Settings2 className="h-3.5 w-3.5" aria-hidden />
                  Edit
                </Link>
              </li>
            ))}
          </ul>
        )}
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
            aria-label={`Clear ${label.toLowerCase()}`}
            data-testid={`execution-clear-${label.toLowerCase().replace(/\s+/g, "-")}`}
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

interface RuntimeSelectProps {
  value: string | null;
  onChange: (next: string | null) => void;
  ariaLabel: string;
  testid: string;
}

function RuntimeSelect({
  value,
  onChange,
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
      className="flex h-9 w-full rounded-md border border-input bg-background px-3 text-sm shadow-sm focus-visible:outline-none focus-visible:ring-1 focus-visible:ring-ring"
    >
      <option value={FIELD_UNSET}>(leave to default)</option>
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

/**
 * Inline credential-status banner — the same pattern as the
 * unit-create wizard Step 1. Reused here so the Execution tab surfaces
 * "provider not configured" at edit time rather than at dispatch.
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
