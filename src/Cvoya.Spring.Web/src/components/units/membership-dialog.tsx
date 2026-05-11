"use client";

import { useEffect, useMemo, useState } from "react";

import { Button } from "@/components/ui/button";
import { Dialog } from "@/components/ui/dialog";
import { Input } from "@/components/ui/input";
import { useModelProviders } from "@/lib/api/queries";
import { formatTranslatedError } from "@/lib/api/translate-error";
import type {
  AgentExecutionMode,
  InstalledModelProviderResponse,
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
   * The existing membership record to pre-populate. Also used for the
   * "agent display name" header. Must be provided when the dialog is open.
   */
  initial: UnitMembershipResponse | null;
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
 * Edit a unit->agent membership. Covers per-membership config: model,
 * specialty, enabled, execution mode, and calling the submit handler
 * with a clean payload.
 *
 * The dialog is state-owned inside this component; the parent only cares
 * about open/close and the final submitted values. That keeps the Agents tab
 * free of form plumbing.
 */
export function MembershipDialog({
  open,
  initial,
  agentDisplayNames = {},
  onCancel,
  onSubmit,
}: MembershipDialogProps) {
  // ADR-0038: model catalogue is sourced from the tenant-installed
  // model providers. The hook returns each provider + its configured
  // model list so the dropdown can render grouped options without a
  // hardcoded fallback. Providers the tenant has not installed are
  // invisible here — the caller can still type any server-accepted
  // value via the "keep current" option below, so an unknown persisted
  // model round-trips losslessly.
  const modelProvidersQuery = useModelProviders();
  const providers = useMemo<InstalledModelProviderResponse[]>(
    () => modelProvidersQuery.data ?? [],
    [modelProvidersQuery.data],
  );

  // Default model: the first installed provider's `defaultModel`
  // (falling back to its first configured model), or the empty string
  // when no providers are installed yet. Resolved lazily via a helper
  // so the useEffect seeding below can read the freshest value each
  // time.
  const defaultModel = useMemo<string>(() => {
    for (const p of providers) {
      if (p.defaultModel) return p.defaultModel;
      if (p.models && p.models.length > 0) return p.models[0];
    }
    return "";
  }, [providers]);

  const [model, setModel] = useState<string>("");
  const [specialty, setSpecialty] = useState("");
  const [enabled, setEnabled] = useState(true);
  const [executionMode, setExecutionMode] = useState<AgentExecutionMode>("Auto");
  const [submitting, setSubmitting] = useState(false);
  const [error, setError] = useState<string | null>(null);

  // Reset local form state whenever the dialog opens or the `initial`
  // prop changes. This matters because the Agents
  // tab re-uses a single <MembershipDialog /> across rows — without this
  // reset, opening edit for row B would show row A's old values.
  useEffect(() => {
    if (!open) return;
    setError(null);
    setSubmitting(false);
    if (!initial) return;
    setModel(initial.model ?? defaultModel);
    setSpecialty(initial.specialty ?? "");
    setEnabled(initial.enabled);
    setExecutionMode(initial.executionMode ?? "Auto");
  }, [open, initial, defaultModel]);

  // Group the dropdown by provider (display name) so operators can see
  // which provider a model comes from. Each provider carries its own
  // configured `models` list; empty lists collapse the optgroup entirely.
  const modelGroups = useMemo(
    () =>
      providers
        .map((p) => ({
          id: p.id,
          label: p.displayName,
          models: p.models ?? [],
        }))
        .filter((g) => g.models.length > 0),
    [providers],
  );

  // Also include the current model value in the dropdown even when the
  // catalogue doesn't know it (server-side the model may be anything,
  // and providers that aren't installed on this tenant aren't surfaced
  // by the model-providers endpoint). Without this, editing a
  // membership whose model is outside the catalogue would silently
  // switch it to the default on next change.
  const isModelInCatalog = useMemo(() => {
    return modelGroups.some((g) => g.models.includes(model));
  }, [model, modelGroups]);

  const headerLabel = useMemo(() => {
    if (!initial) return null;
    return agentDisplayNames[initial.agentAddress] ?? initial.agentAddress;
  }, [initial, agentDisplayNames]);

  const canSubmit = initial !== null;

  const handleSubmit = async () => {
    setError(null);
    if (!initial) {
      setError("Membership details are unavailable.");
      return;
    }
    setSubmitting(true);
    try {
      await onSubmit({
        agentAddress: initial.agentAddress,
        model: model || null,
        specialty: specialty.trim() || null,
        enabled,
        executionMode,
      });
    } catch (err) {
      setError(formatTranslatedError(err));
    } finally {
      setSubmitting(false);
    }
  };

  return (
    <Dialog
      open={open}
      onClose={onCancel}
      title="Edit membership"
      description={`Update per-membership config for ${
        headerLabel ?? "this agent"
      }.`}
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
            {submitting ? "Saving…" : "Save"}
          </Button>
        </>
      }
    >
      <div
        className="rounded-md border border-border bg-muted/30 px-3 py-2 text-sm"
        data-testid="membership-dialog-agent-header"
      >
        <span className="text-xs uppercase tracking-wide text-muted-foreground">
          Agent
        </span>
        <div className="mt-0.5 flex items-center gap-2">
          <span className="font-medium">{headerLabel}</span>
          {initial && (
            <span className="truncate font-mono text-xs text-muted-foreground">
              agent://{initial.agentAddress}
            </span>
          )}
        </div>
      </div>

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
