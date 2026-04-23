"use client";

/**
 * Validation panel on `/units/[name]` (T-07, issue #949).
 *
 * The backend `UnitValidationWorkflow` (T-04) drives units through
 * `Draft → Validating → {Stopped | Error}` without host-side probes.
 * This component is the operator-facing read-out:
 *
 *   - `Validating` → four-step checklist (`PullingImage` →
 *     `VerifyingTool` → `ValidatingCredential` → `ResolvingModel`). The
 *     current step animates as a spinner; earlier steps are checkmarks;
 *     later steps are muted. The server doesn't persist intermediate
 *     progress — only the terminal state — so the panel taps the same
 *     SSE stream `useActivityStream` already consumes to advance the
 *     spinner as `ValidationProgress` events flow in.
 *   - `Error` → structured block reading `unit.lastValidationError`
 *     ({step, code, message}). Renders friendly remediation copy from a
 *     per-code map plus two actions: Retry validation (POST
 *     `/revalidate`) and Edit credential & retry (client-orchestrated
 *     update-credential then revalidate, per T-00 topic 6).
 *   - `Stopped` → "Validation passed" summary + Revalidate button.
 *   - Anything else → panel is hidden.
 *
 * The panel never re-fetches the unit itself — `UnitDetailClient`
 * already holds the query result and passes it in. Cache invalidation
 * on ValidationProgress events is handled by `useActivityStream`.
 */

import { useMemo, useState } from "react";
import {
  AlertTriangle,
  Check,
  CheckCircle2,
  KeyRound,
  Loader2,
  RefreshCw,
} from "lucide-react";
import {
  useMutation,
  useQueryClient,
  type UseMutationResult,
} from "@tanstack/react-query";

import { Button } from "@/components/ui/button";
import { Card, CardContent, CardHeader, CardTitle } from "@/components/ui/card";
import { Input } from "@/components/ui/input";
import { api } from "@/lib/api/client";
import { queryKeys } from "@/lib/api/query-keys";
import { useActivityStream } from "@/lib/stream/use-activity-stream";
import type {
  ActivityEvent,
  UnitResponse,
  UnitValidationError,
  UnitValidationStep,
} from "@/lib/api/types";
import { getRuntimeSecretName } from "@/lib/ai-models";
import { cn } from "@/lib/utils";

interface Props {
  unit: UnitResponse;
  // Image / runtime context needed for a few error-copy strings (e.g.
  // "The <tool> command isn't available in <image>"). Neither sits on
  // `UnitResponse` — the detail page pulls them from
  // `useUnitExecution`. Passed as an optional prop so the panel renders
  // cleanly even when the execution slice hasn't loaded yet.
  image?: string | null;
  runtime?: string | null;
}

/**
 * The four probe steps, in the order the workflow walks them. Keeping
 * the list central so the panel and the friendly-copy map cannot drift.
 *
 * `SchedulingWorkflow` (#1136) is intentionally NOT in this list — it's
 * a host-side step that runs before any in-container probe and is only
 * surfaced when scheduling itself fails. The live checklist still walks
 * the four probe steps; the host-side step only appears as the `step`
 * value on a terminal `Error` panel.
 */
const STEP_ORDER: readonly UnitValidationStep[] = [
  "PullingImage",
  "VerifyingTool",
  "ValidatingCredential",
  "ResolvingModel",
] as const;

const STEP_LABEL: Record<UnitValidationStep, string> = {
  PullingImage: "Pulling image",
  VerifyingTool: "Verifying tool",
  ValidatingCredential: "Validating credential",
  ResolvingModel: "Resolving model",
  SchedulingWorkflow: "Scheduling validation",
};

/**
 * Friendly, operator-oriented remediation copy keyed by
 * `UnitValidationCodes`. One place so i18n is a localized change later
 * (rather than scattered strings across sub-components). Values are
 * functions so an eventual swap to interpolated templates doesn't
 * require widespread call-site edits — each function receives the unit
 * + execution context and returns the rendered string.
 */
interface CopyContext {
  image: string | null;
  runtime: string | null;
  tool: string | null;
  model: string | null;
  runId: string | null;
}

const VALIDATION_COPY: Record<
  string,
  (ctx: CopyContext, err?: UnitValidationError) => string
> = {
  ImagePullFailed: (ctx) =>
    `Could not pull image \`${ctx.image ?? "(unset)"}\`. Check that the registry is reachable and the tag exists.`,
  ImageStartFailed: (ctx) =>
    `The container for \`${ctx.image ?? "(unset)"}\` pulled but failed to start. Check the image's entrypoint and rebuild if needed.`,
  ToolMissing: (ctx) =>
    `The \`${ctx.tool ?? "runtime"}\` command isn't available in \`${ctx.image ?? "(unset)"}\`. Rebuild the image with the tool installed, or pick a different runtime.`,
  CredentialInvalid: () =>
    "The credential was rejected by the runtime. Edit the credential below and retry.",
  CredentialFormatRejected: () =>
    "The credential doesn't match the expected format for this runtime. Check it and retry.",
  ModelNotFound: (ctx) =>
    `Model \`${ctx.model ?? "(unset)"}\` isn't available for this credential. Pick a different model or update access.`,
  ProbeTimeout: (ctx) =>
    ctx.runId
      ? `Validation didn't finish in time. Retry; if it repeats, check dispatcher logs (run id \`${ctx.runId}\`).`
      : "Validation didn't finish in time. Retry; if it repeats, check dispatcher logs.",
  ProbeInternalError: (ctx) =>
    ctx.runId
      ? `Validation failed with an internal error. Retry; if it repeats, check dispatcher logs (run id \`${ctx.runId}\`).`
      : "Validation failed with an internal error. Retry; if it repeats, check dispatcher logs.",
  // #1136 / #1142: scheduler-side failures the actor catches generically
  // (Dapr workflow runtime down, transient infra). Operator action: retry
  // via /revalidate; if it persists, check dispatcher logs.
  ScheduleFailed: () =>
    "The validation workflow couldn't be scheduled. Retry; if it repeats, check dispatcher logs.",
  // #1144: scheduler-side failure the operator can fix on the wizard's
  // Execution step (e.g. no container image, no runtime). The scheduler
  // attaches the missing field name(s) under `details.missing` so we can
  // name them in the copy. Falls back to a generic
  // "configuration is incomplete" string when no detail is set.
  ConfigurationIncomplete: (_ctx, err) => {
    const missing = err?.details?.missing;
    if (typeof missing === "string" && missing.length > 0) {
      const fields = missing
        .split(",")
        .map((f) => f.trim())
        .filter((f) => f.length > 0);
      if (fields.length > 0) {
        return (
          `This unit can't be validated yet — missing: ${fields.join(", ")}. ` +
          "Update the unit's Execution settings and retry validation."
        );
      }
    }
    return "This unit's configuration is incomplete. Update its Execution settings and retry validation.";
  },
};

function formatValidationCopy(
  err: UnitValidationError,
  ctx: CopyContext,
): string {
  const mapped = VALIDATION_COPY[err.code];
  if (mapped) return mapped(ctx, err);
  // Fall back to the server-supplied message so unknown codes still
  // render something actionable.
  return err.message;
}

/**
 * Narrow a `ValidationProgress` activity event's `details` payload to
 * `{ step, status, code? }`. The server serialises these keys as
 * lower-case strings (see `EmitValidationProgressActivity`), so we
 * validate the shape here rather than trusting `unknown`.
 */
function extractProgressStep(event: ActivityEvent): UnitValidationStep | null {
  const details = event.details;
  if (!details || typeof details !== "object") return null;
  const step = (details as { step?: unknown }).step;
  if (typeof step !== "string") return null;
  if (
    step === "PullingImage" ||
    step === "VerifyingTool" ||
    step === "ValidatingCredential" ||
    step === "ResolvingModel"
  ) {
    return step;
  }
  return null;
}

export default function ValidationPanel({ unit, image, runtime }: Props) {
  const status = unit.status;

  // Track the most-recently-observed step from SSE so the spinner
  // advances mid-validation. The server only writes terminal state to
  // the unit row; intermediate progress arrives only over the wire.
  // The panel runs its own filtered `useActivityStream` that both
  // advances `liveStep` and — as a side-effect of the filter
  // predicate — drops the event from the hook's local list (we don't
  // need it; `UnitDetailClient` owns the outer subscription that
  // handles cache invalidation).
  const [liveStep, setLiveStep] = useState<UnitValidationStep | null>(null);

  useActivityStream({
    filter: (event) => {
      if (event.source.scheme !== "unit") return false;
      if (event.source.path !== unit.name) return false;
      if (event.eventType !== "ValidationProgress") return false;
      const step = extractProgressStep(event);
      if (step !== null) {
        setLiveStep(step);
      }
      // Return false so the hook doesn't keep the event in its local
      // list or fire its cache-invalidation pass — the parent's stream
      // already covers that.
      return false;
    },
  });

  const activeStep: UnitValidationStep | null = useMemo(() => {
    if (status !== "Validating") return null;
    return liveStep;
  }, [status, liveStep]);

  const queryClient = useQueryClient();

  const revalidateMutation = useMutation({
    mutationFn: async () => api.revalidateUnit(unit.name),
    onSuccess: () => {
      queryClient.invalidateQueries({
        queryKey: queryKeys.units.detail(unit.name),
      });
    },
  });

  // Panel is hidden for the in-flight lifecycle statuses and Draft.
  if (
    status === "Running" ||
    status === "Starting" ||
    status === "Stopping" ||
    status === "Draft"
  ) {
    return null;
  }

  if (status === "Validating") {
    return (
      <Card data-testid="validation-panel" data-panel-state="validating">
        <CardHeader>
          <CardTitle className="text-base">Validating unit</CardTitle>
        </CardHeader>
        <CardContent>
          <p className="mb-3 text-sm text-muted-foreground">
            The backend is running probes against this unit. Steps complete
            in order; the unit transitions to Stopped on success or Error on
            failure.
          </p>
          <StepChecklist activeStep={activeStep} />
        </CardContent>
      </Card>
    );
  }

  if (status === "Stopped") {
    return (
      <Card data-testid="validation-panel" data-panel-state="stopped">
        <CardHeader>
          <CardTitle className="text-base">Validation passed</CardTitle>
        </CardHeader>
        <CardContent className="space-y-3">
          <p
            role="status"
            className="flex items-start gap-2 rounded-md border border-emerald-500/40 bg-emerald-500/10 px-3 py-2 text-sm text-emerald-900 dark:text-emerald-200"
          >
            <CheckCircle2 className="mt-0.5 h-4 w-4 shrink-0" aria-hidden />
            <span>
              Last validation succeeded. The unit is ready to start.
            </span>
          </p>
          <div className="flex items-center gap-2">
            <Button
              variant="outline"
              size="sm"
              data-testid="validation-panel-revalidate"
              disabled={revalidateMutation.isPending}
              onClick={() => revalidateMutation.mutate()}
            >
              <RefreshCw className="mr-1.5 h-3.5 w-3.5" aria-hidden />
              {revalidateMutation.isPending ? "Starting…" : "Revalidate"}
            </Button>
            {revalidateMutation.isError && (
              <span
                role="alert"
                className="text-xs text-destructive"
                data-testid="validation-panel-revalidate-error"
              >
                {revalidateMutation.error instanceof Error
                  ? revalidateMutation.error.message
                  : "Failed to revalidate."}
              </span>
            )}
          </div>
        </CardContent>
      </Card>
    );
  }

  // status === "Error"
  const err = unit.lastValidationError ?? null;
  const ctx: CopyContext = {
    image: image ?? null,
    runtime: runtime ?? null,
    tool: unit.tool ?? null,
    model: unit.model ?? null,
    runId: unit.lastValidationRunId ?? null,
  };

  return (
    <Card data-testid="validation-panel" data-panel-state="error">
      <CardHeader>
        <CardTitle className="text-base">Validation failed</CardTitle>
      </CardHeader>
      <CardContent className="space-y-3">
        {/*
         * Render the same step checklist used during Validating, but
         * with the failed step marked. This anchors the error block
         * below to the specific step it relates to — operators told us
         * (#issue-handling-stuck-validation) the error felt detached
         * from the visual progress when it appeared as a separate
         * banner.
         */}
        <StepChecklist
          activeStep={null}
          failedStep={err?.step ?? null}
        />

        <div
          role="alert"
          className="space-y-1.5 rounded-md border border-destructive/50 bg-destructive/10 px-3 py-3 text-sm text-foreground"
          data-testid="validation-panel-error"
        >
          <div className="flex items-start gap-2">
            <AlertTriangle
              className="mt-0.5 h-4 w-4 shrink-0 text-destructive"
              aria-hidden
            />
            <div className="flex-1 space-y-1">
              <p className="font-medium">
                Step: {err ? STEP_LABEL[err.step] : "(unknown)"}
                {err && (
                  <span className="ml-2 text-xs font-normal text-muted-foreground">
                    code: <code className="font-mono">{err.code}</code>
                  </span>
                )}
              </p>
              <p
                className="text-sm text-foreground"
                data-testid="validation-panel-error-copy"
              >
                {err
                  ? formatValidationCopy(err, ctx)
                  : "The backend reported an error but didn't attach details."}
              </p>
              {ctx.runId && (
                <p
                  className="text-[11px] text-muted-foreground"
                  data-testid="validation-panel-run-id"
                >
                  run id: <code className="font-mono">{ctx.runId}</code>
                </p>
              )}
            </div>
          </div>
        </div>

        <ErrorActions unit={unit} revalidateMutation={revalidateMutation} />
      </CardContent>
    </Card>
  );
}

// --- Step checklist ---------------------------------------------------

function StepChecklist({
  activeStep,
  failedStep = null,
}: {
  activeStep: UnitValidationStep | null;
  // When set, the checklist renders in a "post-mortem" mode: every
  // step before `failedStep` shows as completed, the failed step
  // shows with a destructive marker, and steps after it are muted
  // ("never reached"). Used by the Error state so the error block
  // below visually attaches to the step that produced it.
  failedStep?: UnitValidationStep | null;
}) {
  // Map each step's position to the visual state. When `activeStep` is
  // null and there's no `failedStep` the server hasn't yet emitted
  // progress — treat the first step as in-flight so the operator sees
  // motion immediately. When `failedStep` is set, ignore `activeStep`
  // entirely.
  const failedIndex = failedStep ? STEP_ORDER.indexOf(failedStep) : -1;
  const activeIndex = failedStep
    ? -1
    : activeStep
      ? STEP_ORDER.indexOf(activeStep)
      : 0;

  return (
    <ol className="space-y-2" data-testid="validation-step-checklist">
      {STEP_ORDER.map((step, idx) => {
        let state: "done" | "active" | "future" | "failed" | "skipped";
        if (failedIndex >= 0) {
          if (idx < failedIndex) state = "done";
          else if (idx === failedIndex) state = "failed";
          else state = "skipped";
        } else if (idx < activeIndex) {
          state = "done";
        } else if (idx === activeIndex) {
          state = "active";
        } else {
          state = "future";
        }
        return (
          <li
            key={step}
            data-step={step}
            data-state={state}
            className={cn(
              "flex items-center gap-2 text-sm",
              (state === "future" || state === "skipped") && "text-muted-foreground",
              state === "failed" && "text-destructive",
            )}
          >
            <span
              aria-hidden
              className={cn(
                "flex h-5 w-5 shrink-0 items-center justify-center rounded-full border",
                state === "done" &&
                  "border-emerald-500/40 bg-emerald-500/15 text-emerald-600 dark:text-emerald-300",
                state === "active" &&
                  "border-primary/50 bg-primary/10 text-primary",
                state === "failed" &&
                  "border-destructive/50 bg-destructive/10 text-destructive",
                (state === "future" || state === "skipped") &&
                  "border-border bg-muted text-muted-foreground",
              )}
            >
              {state === "done" && <Check className="h-3 w-3" />}
              {state === "active" && (
                <Loader2 className="h-3 w-3 animate-spin" />
              )}
              {state === "failed" && <AlertTriangle className="h-3 w-3" />}
            </span>
            <span>
              {STEP_LABEL[step]}
              {state === "active" && (
                <span className="ml-2 text-xs text-muted-foreground">
                  in progress
                </span>
              )}
              {state === "failed" && (
                <span className="ml-2 text-xs text-destructive">failed</span>
              )}
              {state === "skipped" && (
                <span className="ml-2 text-xs text-muted-foreground">
                  skipped
                </span>
              )}
            </span>
          </li>
        );
      })}
    </ol>
  );
}

// --- Error actions ----------------------------------------------------

/**
 * Action row rendered below the structured error block. Two CTAs:
 *
 *  1. Retry validation — POST `/revalidate`.
 *  2. Edit credential & retry — reveal an inline credential input, on
 *     save run a two-call sequence: update the unit-scoped credential
 *     secret, then revalidate. T-00 topic 6 explicitly rejected a
 *     combined "update + revalidate" endpoint for V2, so the
 *     orchestration lives here.
 */
function ErrorActions({
  unit,
  revalidateMutation,
}: {
  unit: UnitResponse;
  revalidateMutation: UseMutationResult<void, Error, void>;
}) {
  const [editing, setEditing] = useState(false);
  const [credentialKey, setCredentialKey] = useState("");
  const queryClient = useQueryClient();

  // The secret the backend reads for this unit's runtime. The runtime
  // id is carried on the unit as `tool` for fixed-provider launchers
  // (claude-code → "claude", etc.) OR `provider` for dapr-agent. We
  // map through `getRuntimeSecretName` so the name stays in sync with
  // the wizard's and the backend's naming conventions.
  const runtimeIdForSecret = resolveRuntimeId(unit);
  const secretName = runtimeIdForSecret
    ? getRuntimeSecretName(runtimeIdForSecret)
    : null;

  const editAndRetryMutation = useMutation<
    void,
    Error,
    { key: string }
  >({
    mutationFn: async ({ key }) => {
      if (!secretName) {
        throw new Error(
          "Can't infer the credential name for this unit's runtime.",
        );
      }
      // Two calls, client-orchestrated (T-00 topic 6). Order matters:
      // write the new secret first so the retry reads it.
      await api.createUnitSecret(unit.name, { name: secretName, value: key });
      await api.revalidateUnit(unit.name);
    },
    onSuccess: () => {
      setEditing(false);
      setCredentialKey("");
      queryClient.invalidateQueries({
        queryKey: queryKeys.units.detail(unit.name),
      });
      queryClient.invalidateQueries({
        queryKey: queryKeys.units.secrets(unit.name),
      });
    },
  });

  return (
    <div className="space-y-2">
      <div className="flex flex-wrap items-center gap-2">
        <Button
          variant="outline"
          size="sm"
          data-testid="validation-panel-retry"
          disabled={revalidateMutation.isPending}
          onClick={() => revalidateMutation.mutate()}
        >
          <RefreshCw className="mr-1.5 h-3.5 w-3.5" aria-hidden />
          {revalidateMutation.isPending ? "Retrying…" : "Retry validation"}
        </Button>
        {!editing && (
          <Button
            variant="outline"
            size="sm"
            data-testid="validation-panel-edit-credential"
            onClick={() => setEditing(true)}
          >
            <KeyRound className="mr-1.5 h-3.5 w-3.5" aria-hidden />
            Edit credential &amp; retry
          </Button>
        )}
      </div>

      {revalidateMutation.isError && (
        <p
          role="alert"
          className="text-xs text-destructive"
          data-testid="validation-panel-retry-error"
        >
          {revalidateMutation.error instanceof Error
            ? revalidateMutation.error.message
            : "Failed to retry."}
        </p>
      )}

      {editing && (
        <div
          className="space-y-2 rounded-md border border-border bg-muted/30 p-3"
          data-testid="validation-panel-credential-editor"
        >
          <label
            htmlFor="validation-panel-credential-input"
            className="block text-xs text-muted-foreground"
          >
            New credential
          </label>
          <Input
            id="validation-panel-credential-input"
            type="password"
            value={credentialKey}
            onChange={(e) => setCredentialKey(e.target.value)}
            placeholder="Paste the replacement key"
            autoComplete="off"
            spellCheck={false}
            data-testid="validation-panel-credential-input"
          />
          <div className="flex items-center gap-2">
            <Button
              size="sm"
              data-testid="validation-panel-credential-save"
              disabled={
                editAndRetryMutation.isPending ||
                credentialKey.trim().length === 0
              }
              onClick={() =>
                editAndRetryMutation.mutate({ key: credentialKey.trim() })
              }
            >
              {editAndRetryMutation.isPending
                ? "Saving & retrying…"
                : "Save & retry"}
            </Button>
            <Button
              variant="outline"
              size="sm"
              data-testid="validation-panel-credential-cancel"
              disabled={editAndRetryMutation.isPending}
              onClick={() => {
                setEditing(false);
                setCredentialKey("");
              }}
            >
              Cancel
            </Button>
          </div>
          {editAndRetryMutation.isError && (
            <p
              role="alert"
              className="text-xs text-destructive"
              data-testid="validation-panel-credential-error"
            >
              {editAndRetryMutation.error instanceof Error
                ? editAndRetryMutation.error.message
                : "Failed to save credential."}
            </p>
          )}
        </div>
      )}
    </div>
  );
}

/**
 * Resolve the runtime id the unit's credential is keyed against.
 * Mirrors `deriveRequiredCredentialRuntime` in the wizard but without
 * the agent-runtimes catalog — the panel only needs the id, and the
 * secret name is derived from it via `getRuntimeSecretName`. Returns
 * null when the unit runs a custom tool (no declared credential).
 */
function resolveRuntimeId(unit: UnitResponse): string | null {
  const tool = unit.tool;
  const provider = unit.provider ?? "";
  if (!tool) return null;
  switch (tool) {
    case "claude-code":
      return "claude";
    case "codex":
      return "openai";
    case "gemini":
      return "google";
    case "dapr-agent": {
      const normalised = provider.trim().toLowerCase();
      if (!normalised || normalised === "ollama") return null;
      return normalised === "anthropic" ? "claude" : normalised;
    }
    case "custom":
    default:
      return null;
  }
}
