"use client";

import { useCallback, useMemo, useRef, useState } from "react";
import { useQuery, useQueryClient } from "@tanstack/react-query";
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
import type { UnitResponse } from "@/lib/api/types";
import { buildAgentPackageYaml } from "./build-agent-package";

// ---------------------------------------------------------------------------
// Types
// ---------------------------------------------------------------------------

interface FormState {
  id: string;
  displayName: string;
  role: string;
  description: string;
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
  description: "",
  executionTool: DEFAULT_EXECUTION_TOOL,
  image: "",
  runtime: "",
  model: "",
  unitIds: [],
};

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
}

const INITIAL_INSTALL: InstallState = {
  installId: null,
  agentId: null,
  phase: "idle",
  error: null,
  membershipErrors: {},
  membershipDone: [],
};

const POLL_INTERVAL_MS = 2_000;
const POLL_TIMEOUT_MS = 120_000;

// ---------------------------------------------------------------------------
// Page
// ---------------------------------------------------------------------------

/**
 * New-agent wizard — scratch path (ADR-0035 decision 6).
 *
 * On submit the wizard builds an `AgentPackage` YAML in memory and
 * POSTs it to `POST /api/v1/packages/install/file`, the same two-phase
 * install endpoint the CLI uses. Unit assignments are wired as post-install
 * side-effects: once the install reaches `active`, the page fires
 * `POST /api/v1/units/{id}/agents/{agentId}` for each selected unit,
 * sequentially, with per-unit progress feedback.
 *
 * Full AgentPackage activation in the install pipeline is tracked in #1559.
 *
 * Visual chrome reuses the existing Card / Input / Button primitives so
 * no new patterns are needed in DESIGN.md.
 */
export default function CreateAgentPage() {
  const router = useRouter();
  const queryClient = useQueryClient();
  const { toast } = useToast();

  const [form, setForm] = useState<FormState>(INITIAL_FORM);
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
  };

  // ── Queries ────────────────────────────────────────────────────────────

  const unitsQuery = useQuery<UnitResponse[]>({
    queryKey: queryKeys.units.list(),
    queryFn: () => api.listUnits(),
    staleTime: 30_000,
  });

  const runtimesQuery = useAgentRuntimes();
  const runtimes = useMemo(() => runtimesQuery.data ?? [], [runtimesQuery.data]);

  const toolRuntimeId = getToolRuntimeId(form.executionTool);
  const modelsQuery = useAgentRuntimeModels(toolRuntimeId ?? "", {
    enabled: Boolean(toolRuntimeId),
  });

  const modelOptions = useMemo(() => {
    if (toolRuntimeId) {
      const list = modelsQuery.data ?? [];
      return list.map((m) => ({ id: m.id, label: m.displayName ?? m.id }));
    }
    return runtimes.flatMap((r) =>
      (r.models ?? []).map((m) => ({
        id: m,
        label: `${m} — ${r.displayName}`,
      })),
    );
  }, [toolRuntimeId, modelsQuery.data, runtimes]);

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
   */
  const addMemberships = useCallback(
    async (agentId: string, unitIds: string[]) => {
      const errors: Record<string, string> = {};
      const done: string[] = [];
      for (const unitId of unitIds) {
        try {
          await api.assignUnitAgent(unitId, agentId);
          done.push(unitId);
        } catch (err) {
          const msg = err instanceof Error ? err.message : String(err);
          errors[unitId] = msg;
        }
        setInstall((prev) => ({
          ...prev,
          membershipErrors: { ...errors },
          membershipDone: [...done],
        }));
      }
      return { errors, done };
    },
    [],
  );

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
    });

    let installId: string;
    let alreadyActive = false;
    try {
      const yaml = buildAgentPackageYaml({
        id: agentId,
        displayName: form.displayName.trim(),
        role: form.role.trim() || undefined,
        description: form.description.trim() || undefined,
        image: form.image.trim() || undefined,
        runtime: form.runtime.trim() || undefined,
        tool: form.executionTool || undefined,
        model: form.model.trim() || undefined,
        unitIds,
      });
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
      const { errors } = await addMemberships(agentId, unitIds);

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
    queryClient.invalidateQueries({ queryKey: queryKeys.agents.all });
    queryClient.invalidateQueries({ queryKey: queryKeys.units.all });
    queryClient.invalidateQueries({ queryKey: queryKeys.dashboard.all });
    queryClient.invalidateQueries({ queryKey: queryKeys.tenant.tree() });

    toast({ title: "Agent created", description: agentId });

    const target = unitIds[0]?.trim();
    if (target) {
      router.push(`/units?node=${encodeURIComponent(target)}&tab=Agents`);
    } else {
      router.push("/units");
    }
  }, [form, addMemberships, pollUntilTerminal, queryClient, router, toast]);

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
          const { errors } = await addMemberships(install.agentId!, form.unitIds);
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
        queryClient.invalidateQueries({ queryKey: queryKeys.agents.all });
        queryClient.invalidateQueries({ queryKey: queryKeys.units.all });
        queryClient.invalidateQueries({ queryKey: queryKeys.dashboard.all });
        queryClient.invalidateQueries({ queryKey: queryKeys.tenant.tree() });
        toast({ title: "Agent created", description: install.agentId ?? "" });
        const target = form.unitIds[0]?.trim();
        if (target) router.push(`/units?node=${encodeURIComponent(target)}&tab=Agents`);
        else router.push("/units");
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

  // ── Derived UI state ───────────────────────────────────────────────────

  const submitting =
    install.phase === "installing" ||
    install.phase === "polling" ||
    install.phase === "memberships";

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
            Builds an{" "}
            <code className="rounded bg-muted px-1 py-0.5 text-xs font-mono">
              AgentPackage
            </code>{" "}
            and installs it through the same pipeline as{" "}
            <code className="rounded bg-muted px-1 py-0.5 text-xs font-mono">
              spring package install
            </code>
            .
          </p>
        </div>
        <Button
          variant="outline"
          size="sm"
          onClick={() => {
            pollAbortRef.current?.abort();
            router.back();
          }}
          disabled={submitting}
        >
          <ArrowLeft className="mr-1 h-4 w-4" />
          Back
        </Button>
      </div>

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
        <Card className="mt-4">
          <CardHeader>
            <CardTitle>Execution</CardTitle>
          </CardHeader>
          <CardContent className="space-y-4">
            <label className="block space-y-1">
              <span className="text-sm text-muted-foreground">Execution tool</span>
              <select
                value={form.executionTool}
                onChange={(e) => {
                  const tool = e.target.value as ExecutionTool;
                  setForm((prev) => ({ ...prev, executionTool: tool, model: "" }));
                  setValidationMessage(null);
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
            onClick={() => {
              pollAbortRef.current?.abort();
              router.back();
            }}
            disabled={submitting}
          >
            Cancel
          </Button>
          <Button
            type="submit"
            disabled={submitting || install.phase === "install-failed"}
          >
            {phaseLabel[install.phase]}
          </Button>
        </div>
      </form>
    </div>
  );
}
