"use client";

import { useMemo, useState } from "react";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { useRouter } from "next/navigation";
import Link from "next/link";
import { ArrowLeft } from "lucide-react";

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
import {
  useAgentRuntimeModels,
  useAgentRuntimes,
} from "@/lib/api/queries";
import { queryKeys } from "@/lib/api/query-keys";
import {
  AGENT_NAME_PATTERN,
  buildCreateAgentRequest,
  describeAgentCreateError,
  validateAgentCreateInput,
} from "@/lib/agents/create-agent";
import {
  DEFAULT_EXECUTION_TOOL,
  EXECUTION_TOOLS,
  getToolRuntimeId,
  type ExecutionTool,
} from "@/lib/ai-models";
import { EXECUTION_RUNTIMES } from "@/lib/api/types";
import type { AgentResponse, UnitResponse } from "@/lib/api/types";

interface FormState {
  id: string;
  displayName: string;
  role: string;
  executionTool: ExecutionTool;
  image: string;
  runtime: string;
  model: string;
  unitIds: string[];
}

const INITIAL_FORM: FormState = {
  id: "",
  displayName: "",
  role: "",
  executionTool: DEFAULT_EXECUTION_TOOL,
  image: "",
  runtime: "",
  model: "",
  unitIds: [],
};

/**
 * Standalone "create agent" page (#1040). Mirrors the CLI's
 * `spring agent create <id> --name --role --unit ... --image --runtime
 * --tool --definition` field-for-field so portal and CLI stay at parity
 * (CONVENTIONS.md § ui-cli-parity).
 *
 * The form deliberately stays a single step — agent creation has fewer
 * decisions than unit creation (no template / scratch / yaml fork), and
 * a wizard would feel ceremonial. Visual chrome reuses the existing
 * Card / Input / Button primitives so no new patterns land in
 * `DESIGN.md`.
 *
 * Submission flows through `buildCreateAgentRequest` (shared with the
 * inline-create dialog in the unit Agents tab) so the wire body for
 * `POST /api/v1/agents` is constructed in exactly one place.
 *
 * On 201:
 *   - The agents/units/dashboard caches are invalidated so the new agent
 *     is visible immediately.
 *   - The router redirects to the parent unit's Agents tab (the agent
 *     detail route lives under the Explorer; the parent unit is the
 *     most useful landing page after a fresh agent appears).
 */
export default function CreateAgentPage() {
  const router = useRouter();
  const queryClient = useQueryClient();
  const { toast } = useToast();

  const [form, setForm] = useState<FormState>(INITIAL_FORM);
  const [submitError, setSubmitError] = useState<string | null>(null);
  const [validationMessage, setValidationMessage] = useState<string | null>(
    null,
  );

  const update = <K extends keyof FormState>(key: K, value: FormState[K]) => {
    setForm((prev) => ({ ...prev, [key]: value }));
    setValidationMessage(null);
    setSubmitError(null);
  };

  // Available units the operator can assign the new agent to. Reuses
  // the existing list endpoint the wizard's parent picker rides; the
  // server already enforces tenant visibility so this is the entire
  // assignable set.
  const unitsQuery = useQuery<UnitResponse[]>({
    queryKey: queryKeys.units.list(),
    queryFn: () => api.listUnits(),
    staleTime: 30_000,
  });

  // Runtime catalog. Drives the model dropdown via the
  // `getToolRuntimeId` mapping — the same hook the unit wizard uses
  // (#735). When the operator picks `dapr-agent` or `custom`, no
  // specific runtime maps cleanly; the dropdown falls back to "all
  // installed runtimes" so the user can still pick a model.
  const runtimesQuery = useAgentRuntimes();
  const runtimes = useMemo(
    () => runtimesQuery.data ?? [],
    [runtimesQuery.data],
  );

  const toolRuntimeId = getToolRuntimeId(form.executionTool);
  const modelsQuery = useAgentRuntimeModels(toolRuntimeId ?? "", {
    enabled: Boolean(toolRuntimeId),
  });

  const modelOptions = useMemo(() => {
    if (toolRuntimeId) {
      const list = modelsQuery.data ?? [];
      return list.map((m) => ({ id: m.id, label: m.displayName ?? m.id }));
    }
    // Fallback: show every model from every installed runtime, grouped
    // by the runtime label. Matches the inline membership-dialog
    // behaviour for "no canonical runtime" tools.
    return runtimes.flatMap((r) =>
      (r.models ?? []).map((m) => ({
        id: m,
        label: `${m} — ${r.displayName}`,
      })),
    );
  }, [toolRuntimeId, modelsQuery.data, runtimes]);

  const createAgent = useMutation({
    mutationFn: async (): Promise<AgentResponse> => {
      const body = buildCreateAgentRequest({
        id: form.id,
        displayName: form.displayName,
        role: form.role,
        unitIds: form.unitIds,
        image: form.image,
        runtime: form.runtime,
        tool: form.executionTool,
        model: form.model,
      });
      return await api.createAgent(body);
    },
    onMutate: () => {
      setSubmitError(null);
    },
    onSuccess: (agent) => {
      queryClient.invalidateQueries({ queryKey: queryKeys.agents.all });
      queryClient.invalidateQueries({ queryKey: queryKeys.units.all });
      queryClient.invalidateQueries({ queryKey: queryKeys.dashboard.all });
      queryClient.invalidateQueries({ queryKey: queryKeys.tenant.tree() });
      toast({ title: "Agent created", description: agent.name });

      // Land on the parent unit's Agents tab so the operator sees the
      // freshly-assigned membership row. The first unit in the list
      // acts as the derived primary on the wire (see
      // `AgentEndpoints.CreateAgentAsync`), so the same one wins here.
      const target = form.unitIds[0]?.trim();
      if (target) {
        router.push(
          `/units?node=${encodeURIComponent(target)}&tab=Agents`,
        );
      } else {
        // Defensive fallback. The form-level validation guarantees we
        // always have at least one unit, but the explorer roster is the
        // safe landing page if we ever reach here.
        router.push("/units");
      }
    },
    onError: (err) => {
      const message = err instanceof Error ? err.message : String(err);
      setSubmitError(message);
      toast({
        title: "Failed to create agent",
        description: message,
        variant: "destructive",
      });
    },
  });

  const handleSubmit = (e: React.FormEvent<HTMLFormElement>) => {
    e.preventDefault();
    const validation = validateAgentCreateInput({
      id: form.id,
      displayName: form.displayName,
      unitIds: form.unitIds,
    });
    if (validation !== null) {
      setValidationMessage(describeAgentCreateError(validation));
      return;
    }
    setValidationMessage(null);
    createAgent.mutate();
  };

  const submitting = createAgent.isPending;

  return (
    <div className="mx-auto flex w-full max-w-3xl flex-col gap-6 px-4 py-8 sm:px-6 lg:px-8">
      <Breadcrumbs
        items={[
          { label: "Dashboard", href: "/" },
          { label: "Units", href: "/units" },
          { label: "New agent" },
        ]}
      />

      <div className="flex items-center justify-between gap-4">
        <div className="space-y-1">
          <h1 className="text-2xl font-semibold tracking-tight">
            Create a new agent
          </h1>
          <p className="text-sm text-muted-foreground">
            Mirrors{" "}
            <code className="rounded bg-muted px-1 py-0.5 text-xs font-mono">
              spring agent create
            </code>
            . The agent is registered in the directory and immediately
            assigned to the units you pick.
          </p>
        </div>
        <Button
          variant="outline"
          size="sm"
          onClick={() => router.back()}
          disabled={submitting}
        >
          <ArrowLeft className="mr-1 h-4 w-4" />
          Back
        </Button>
      </div>

      <form onSubmit={handleSubmit} noValidate>
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
              <span className="text-sm text-muted-foreground">
                Role (optional)
              </span>
              <Input
                value={form.role}
                onChange={(e) => update("role", e.target.value)}
                placeholder="reviewer"
                aria-label="Role"
                disabled={submitting}
              />
            </label>
          </CardContent>
        </Card>

        <Card className="mt-4">
          <CardHeader>
            <CardTitle>Execution</CardTitle>
          </CardHeader>
          <CardContent className="space-y-4">
            <label className="block space-y-1">
              <span className="text-sm text-muted-foreground">
                Execution tool
              </span>
              <select
                value={form.executionTool}
                onChange={(e) => {
                  const tool = e.target.value as ExecutionTool;
                  setForm((prev) => ({
                    ...prev,
                    executionTool: tool,
                    model: "",
                  }));
                  setValidationMessage(null);
                  setSubmitError(null);
                }}
                aria-label="Execution tool"
                disabled={submitting}
                className="flex h-9 w-full rounded-md border border-input bg-background px-3 text-sm shadow-sm focus-visible:outline-none focus-visible:ring-1 focus-visible:ring-ring disabled:cursor-not-allowed disabled:opacity-50"
              >
                {EXECUTION_TOOLS.map((t) => (
                  <option key={t.id} value={t.id}>
                    {t.label}
                  </option>
                ))}
              </select>
              <span className="block text-xs text-muted-foreground">
                Determines which agent runtime processes work. Mirrors{" "}
                <code className="font-mono">--tool</code>.
              </span>
            </label>

            <label className="block space-y-1">
              <span className="text-sm text-muted-foreground">
                Container image (optional)
              </span>
              <Input
                value={form.image}
                onChange={(e) => update("image", e.target.value)}
                placeholder="ghcr.io/example/agent:latest"
                aria-label="Container image"
                disabled={submitting}
              />
              <span className="block text-xs text-muted-foreground">
                Persisted under{" "}
                <code className="font-mono">execution.image</code>. Mirrors{" "}
                <code className="font-mono">--image</code>.
              </span>
            </label>

            <label className="block space-y-1">
              <span className="text-sm text-muted-foreground">
                Container runtime (optional)
              </span>
              <select
                value={form.runtime}
                onChange={(e) => update("runtime", e.target.value)}
                aria-label="Container runtime"
                disabled={submitting}
                className="flex h-9 w-full rounded-md border border-input bg-background px-3 text-sm shadow-sm focus-visible:outline-none focus-visible:ring-1 focus-visible:ring-ring disabled:cursor-not-allowed disabled:opacity-50"
              >
                <option value="">Inherit from unit</option>
                {EXECUTION_RUNTIMES.map((r) => (
                  <option key={r} value={r}>
                    {r}
                  </option>
                ))}
              </select>
              <span className="block text-xs text-muted-foreground">
                Persisted under{" "}
                <code className="font-mono">execution.runtime</code>. Mirrors{" "}
                <code className="font-mono">--runtime</code>.
              </span>
            </label>

            <label className="block space-y-1">
              <span className="text-sm text-muted-foreground">
                Model (optional)
              </span>
              <select
                value={form.model}
                onChange={(e) => update("model", e.target.value)}
                aria-label="Model"
                disabled={submitting || modelOptions.length === 0}
                className="flex h-9 w-full rounded-md border border-input bg-background px-3 text-sm shadow-sm focus-visible:outline-none focus-visible:ring-1 focus-visible:ring-ring disabled:cursor-not-allowed disabled:opacity-50"
              >
                <option value="">Inherit from unit / runtime default</option>
                {modelOptions.map((m) => (
                  <option key={m.id} value={m.id}>
                    {m.label}
                  </option>
                ))}
              </select>
              {modelOptions.length === 0 && !runtimesQuery.isPending && (
                <span className="block text-xs text-muted-foreground">
                  No models available for this tool. The agent will inherit the
                  unit&apos;s default model at dispatch.
                </span>
              )}
            </label>
          </CardContent>
        </Card>

        <Card className="mt-4">
          <CardHeader>
            <CardTitle>Unit assignment</CardTitle>
          </CardHeader>
          <CardContent className="space-y-3">
            <p className="text-xs text-muted-foreground">
              Every agent must belong to at least one unit (#744). The first
              unit in the list becomes the derived primary on the wire.
              Mirrors <code className="font-mono">--unit</code>.
            </p>

            {unitsQuery.isPending ? (
              <p className="text-sm text-muted-foreground">Loading units…</p>
            ) : unitsQuery.isError ? (
              <p className="rounded-md border border-destructive/50 bg-destructive/10 px-3 py-2 text-sm text-destructive">
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
                          setSubmitError(null);
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

        {(validationMessage || submitError) && (
          <p
            role="alert"
            className="mt-4 rounded-md border border-destructive/50 bg-destructive/10 px-3 py-2 text-sm text-destructive"
            data-testid="agent-create-error"
          >
            {validationMessage ?? submitError}
          </p>
        )}

        <div className="mt-6 flex items-center justify-end gap-2">
          <Button
            type="button"
            variant="outline"
            onClick={() => router.back()}
            disabled={submitting}
          >
            Cancel
          </Button>
          <Button type="submit" disabled={submitting}>
            {submitting ? "Creating…" : "Create agent"}
          </Button>
        </div>
      </form>
    </div>
  );
}
