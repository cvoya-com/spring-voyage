"use client";

import { useEffect, useMemo, useState } from "react";

import { Button } from "@/components/ui/button";
import { Dialog } from "@/components/ui/dialog";
import { Input } from "@/components/ui/input";
import { useAgentRuntimes } from "@/lib/api/queries";
import type {
  AgentExecutionMode,
  AgentResponse,
  InstalledAgentRuntimeResponse,
  UnitMembershipResponse,
} from "@/lib/api/types";

const EXECUTION_MODES: AgentExecutionMode[] = ["Auto", "OnDemand"];

export interface MembershipFormValues {
  agentAddress: string;
  model: string | null;
  specialty: string | null;
  enabled: boolean;
  executionMode: AgentExecutionMode;
}

interface MembershipDialogProps {
  open: boolean;
  /**
   * The unit id. Only used to title the dialog (the actual PUT goes through
   * the caller's `onSubmit`); keeping it here so we can render meaningful
   * copy without prop drilling the unit name through.
   */
  unitLabel: string;
  /**
   * Mode switches the agent picker on/off. In "add" mode the user chooses an
   * agent from `assignableAgents`; in "edit" mode the agent is fixed and we
   * show its display name read-only.
   */
  mode: "add" | "edit";
  /**
   * Agents that are NOT already members of this unit. Only consulted in
   * `add` mode; ignored in `edit` mode.
   */
  assignableAgents?: AgentResponse[];
  /**
   * For edit mode: the existing membership record to pre-populate. Also
   * used for the "agent display name" header. Must be provided in edit
   * mode.
   */
  initial?: UnitMembershipResponse | null;
  /**
   * Display-name lookup by agent address. The membership payload only
   * carries the address; the dialog uses this map to show a friendlier
   * header label.
   */
  agentDisplayNames?: Record<string, string>;
  onCancel: () => void;
  onSubmit: (values: MembershipFormValues) => Promise<void>;
}

/**
 * Add/edit a unit→agent membership. Covers:
 *
 *  - Picking an agent (add mode only) from the list of agents that aren't
 *    already in the unit.
 *  - Per-membership config: model, specialty, enabled, execution mode.
 *  - Calling the submit handler with a clean payload.
 *
 * The dialog is state-owned inside this component; the parent only cares
 * about open/close and the final submitted values. That keeps the Agents tab
 * free of form plumbing and makes this dialog reusable from elsewhere if
 * we ever need to.
 */
export function MembershipDialog({
  open,
  unitLabel,
  mode,
  assignableAgents = [],
  initial,
  agentDisplayNames = {},
  onCancel,
  onSubmit,
}: MembershipDialogProps) {
  // #690 / #735: model catalog is sourced from the tenant-installed agent
  // runtimes. The hook returns the runtimes + their configured model lists
  // so the dropdown can render grouped options without a hardcoded
  // fallback. Runtimes the tenant has not installed are invisible here —
  // the caller can still type any server-accepted value via the "keep
  // current" option below, so an unknown persisted model round-trips
  // losslessly.
  const agentRuntimesQuery = useAgentRuntimes();
  const runtimes = useMemo<InstalledAgentRuntimeResponse[]>(
    () => agentRuntimesQuery.data ?? [],
    [agentRuntimesQuery.data],
  );

  // Default model: the first installed runtime's `defaultModel` (falling
  // back to its first configured model), or the empty string when no
  // runtimes are installed yet. Resolved lazily via a helper so the
  // useEffect seeding below can read the freshest value each time.
  const defaultModel = useMemo<string>(() => {
    for (const r of runtimes) {
      if (r.defaultModel) return r.defaultModel;
      if (r.models && r.models.length > 0) return r.models[0];
    }
    return "";
  }, [runtimes]);

  const [agentAddress, setAgentAddress] = useState("");
  const [model, setModel] = useState<string>("");
  const [specialty, setSpecialty] = useState("");
  const [enabled, setEnabled] = useState(true);
  const [executionMode, setExecutionMode] = useState<AgentExecutionMode>("Auto");
  const [submitting, setSubmitting] = useState(false);
  const [error, setError] = useState<string | null>(null);

  // Reset local form state whenever the dialog opens (add mode) or the
  // `initial` prop changes (edit mode). This matters because the Agents
  // tab re-uses a single <MembershipDialog /> across rows — without this
  // reset, opening edit for row B would show row A's old values.
  useEffect(() => {
    if (!open) return;
    setError(null);
    setSubmitting(false);
    if (mode === "edit" && initial) {
      setAgentAddress(initial.agentAddress);
      setModel(initial.model ?? defaultModel);
      setSpecialty(initial.specialty ?? "");
      setEnabled(initial.enabled);
      setExecutionMode(initial.executionMode ?? "Auto");
    } else {
      setAgentAddress("");
      setModel(defaultModel);
      setSpecialty("");
      setEnabled(true);
      setExecutionMode("Auto");
    }
  }, [open, mode, initial, defaultModel]);

  // Group the dropdown by runtime (display name) so operators can see
  // which provider a model comes from. Each runtime carries its own
  // configured `models` list; empty lists collapse the optgroup entirely.
  const modelGroups = useMemo(
    () =>
      runtimes
        .map((r) => ({
          id: r.id,
          label: r.displayName,
          models: r.models ?? [],
        }))
        .filter((g) => g.models.length > 0),
    [runtimes],
  );

  // Also include the current model value in the dropdown even when the
  // catalog doesn't know it (server-side the model may be anything, and
  // runtimes that aren't installed on this tenant aren't surfaced by the
  // agent-runtimes endpoint). Without this, editing a membership whose
  // model is outside the catalog would silently switch it to the default
  // on next change.
  const isModelInCatalog = useMemo(() => {
    return modelGroups.some((g) => g.models.includes(model));
  }, [model, modelGroups]);

  const headerLabel = useMemo(() => {
    if (mode === "edit" && initial) {
      return agentDisplayNames[initial.agentAddress] ?? initial.agentAddress;
    }
    return null;
  }, [mode, initial, agentDisplayNames]);

  const canSubmit =
    mode === "edit" ? true : agentAddress.trim().length > 0;

  const handleSubmit = async () => {
    setError(null);
    if (!canSubmit) {
      setError("Pick an agent to assign.");
      return;
    }
    setSubmitting(true);
    try {
      await onSubmit({
        agentAddress:
          mode === "edit" && initial ? initial.agentAddress : agentAddress,
        model: model || null,
        specialty: specialty.trim() || null,
        enabled,
        executionMode,
      });
    } catch (err) {
      setError(err instanceof Error ? err.message : String(err));
    } finally {
      setSubmitting(false);
    }
  };

  return (
    <Dialog
      open={open}
      onClose={onCancel}
      title={mode === "edit" ? "Edit membership" : "Add agent to unit"}
      description={
        mode === "edit"
          ? `Update per-membership config for ${headerLabel ?? "this agent"}.`
          : `Choose an agent and configure how it behaves inside ${unitLabel}.`
      }
      footer={
        <>
          <Button variant="outline" onClick={onCancel} disabled={submitting}>
            Cancel
          </Button>
          <Button
            onClick={() => {
              void handleSubmit();
            }}
            disabled={submitting || !canSubmit}
          >
            {submitting ? "Saving…" : mode === "edit" ? "Save" : "Add agent"}
          </Button>
        </>
      }
    >
      {mode === "add" ? (
        <label className="block space-y-1">
          <span className="text-sm text-muted-foreground">Agent</span>
          <select
            value={agentAddress}
            onChange={(e) => setAgentAddress(e.target.value)}
            aria-label="Agent"
            disabled={submitting || assignableAgents.length === 0}
            className="flex h-9 w-full rounded-md border border-input bg-background px-3 text-sm shadow-sm focus-visible:outline-none focus-visible:ring-1 focus-visible:ring-ring disabled:cursor-not-allowed disabled:opacity-50"
          >
            <option value="">
              {assignableAgents.length === 0
                ? "No agents available to add"
                : "Pick an agent…"}
            </option>
            {assignableAgents.map((a) => (
              <option key={a.name} value={a.name}>
                {a.displayName || a.name}
              </option>
            ))}
          </select>
        </label>
      ) : (
        <div className="rounded-md border border-border bg-muted/30 px-3 py-2 text-sm">
          <span className="text-muted-foreground">Agent: </span>
          <span className="font-medium">{headerLabel}</span>
        </div>
      )}

      <label className="block space-y-1">
        <span className="text-sm text-muted-foreground">Model</span>
        <select
          value={model}
          onChange={(e) => setModel(e.target.value)}
          aria-label="Model"
          disabled={submitting}
          className="flex h-9 w-full rounded-md border border-input bg-background px-3 text-sm shadow-sm focus-visible:outline-none focus-visible:ring-1 focus-visible:ring-ring disabled:cursor-not-allowed disabled:opacity-50"
        >
          {!isModelInCatalog && model && (
            <option value={model}>{model} (current)</option>
          )}
          {modelGroups.map((g) => (
            <optgroup key={g.id} label={g.label}>
              {g.models.map((m) => (
                <option key={m} value={m}>
                  {m}
                </option>
              ))}
            </optgroup>
          ))}
        </select>
      </label>

      <label className="block space-y-1">
        <span className="text-sm text-muted-foreground">Specialty</span>
        <Input
          value={specialty}
          onChange={(e) => setSpecialty(e.target.value)}
          placeholder="e.g. reviewer"
          disabled={submitting}
          aria-label="Specialty"
        />
      </label>

      <label className="block space-y-1">
        <span className="text-sm text-muted-foreground">Execution mode</span>
        <select
          value={executionMode}
          onChange={(e) =>
            setExecutionMode(e.target.value as AgentExecutionMode)
          }
          aria-label="Execution mode"
          disabled={submitting}
          className="flex h-9 w-full rounded-md border border-input bg-background px-3 text-sm shadow-sm focus-visible:outline-none focus-visible:ring-1 focus-visible:ring-ring disabled:cursor-not-allowed disabled:opacity-50"
        >
          {EXECUTION_MODES.map((m) => (
            <option key={m} value={m}>
              {m}
            </option>
          ))}
        </select>
      </label>

      <label className="flex items-center gap-2 text-sm">
        <input
          type="checkbox"
          checked={enabled}
          onChange={(e) => setEnabled(e.target.checked)}
          disabled={submitting}
          aria-label="Enabled"
        />
        <span>Enabled</span>
      </label>

      {error && (
        <p
          role="alert"
          className="rounded-md border border-destructive/50 bg-destructive/10 px-3 py-2 text-sm text-destructive"
        >
          {error}
        </p>
      )}
    </Dialog>
  );
}
