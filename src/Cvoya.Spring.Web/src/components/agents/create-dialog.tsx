"use client";

import { Dialog } from "@/components/ui/dialog";
import { AgentCreateForm } from "@/components/agents/create-form";

// ---------------------------------------------------------------------------
// Props
// ---------------------------------------------------------------------------

export interface AgentCreateDialogProps {
  /**
   * URL-safe unit id (the form's `unitIds[]` shape — `unit.name`, not the
   * Guid `unit.id`). Pre-selected in the wrapped `<AgentCreateForm>` via
   * `initialUnitIds`. The dialog binds the agent to this unit; operators
   * cannot remove it from within the dialog (the `/agents/create` page is
   * the surface for multi-unit assignment).
   */
  unitId: string;
  /**
   * Human-readable unit name surfaced in the dialog header and the
   * confirmation strip. This is the parent unit's `displayName`; supplied
   * by the caller because the dialog does not query the unit row itself.
   * Replaces the bare-Guid header copy that ADR-0039's design audit
   * (§2.5) flagged on the legacy `<MembershipDialog>` (`#1763`).
   */
  unitDisplayName: string;
  /** Whether the dialog is visible. */
  open: boolean;
  /**
   * Called when the dialog requests to close — ESC, backdrop click, the
   * form's `onCancel`, or a successful create. Mirrors the existing
   * `<Dialog>` open/close contract.
   */
  onOpenChange: (open: boolean) => void;
}

// ---------------------------------------------------------------------------
// Component
// ---------------------------------------------------------------------------

/**
 * Unit-tab "Create agent" dialog shell (ADR-0039 J1).
 *
 * Wraps `<AgentCreateForm>` with the dialog chrome the unit Agents tab
 * needs: a header that names the parent unit (no raw Guid, per the design
 * audit), a confirmation strip telling the operator which unit they are
 * creating into, and close-on-success / close-on-cancel wiring.
 *
 * The form itself stays unchanged — the dialog supplies `initialUnitIds`
 * so the unit checkbox is pre-checked, and the strip above the form
 * confirms the assignment so operators do not have to scroll the form to
 * verify it. The form's cancel and success callbacks both close the
 * dialog via `onOpenChange(false)`; the `<AgentCreateForm>` already
 * handles toast / cache invalidation.
 *
 * Visual contract: see `docs/design/v0.1/agent-create-redesign.md` §2.5
 * (header copy + confirmation strip) and DESIGN.md §12.6 (the inherit-
 * from-parent indicator pattern the form will reuse on later J-phase
 * tasks). This shell does not introduce new visual tokens — it composes
 * the existing `<Dialog>` primitive (`src/components/ui/dialog.tsx`).
 */
export function AgentCreateDialog({
  unitId,
  unitDisplayName,
  open,
  onOpenChange,
}: AgentCreateDialogProps) {
  const close = () => onOpenChange(false);

  return (
    <Dialog
      open={open}
      onClose={close}
      title={`Create agent in ${unitDisplayName}`}
      description={`This agent will be registered in ${unitDisplayName} and inherits its execution defaults. Override below if needed.`}
      className="max-w-2xl"
    >
      {/* Confirmation strip — design §2.5. The parent unit is fixed; the
         operator cannot remove it from within the dialog. The form still
         renders its multi-select below (pre-checked via initialUnitIds);
         the strip is the operator's at-a-glance confirmation that the
         agent is being created into the right unit. */}
      <div
        className="rounded-md border border-border bg-muted/30 px-3 py-2 text-sm"
        data-testid="agent-create-dialog-unit-strip"
      >
        <span className="text-xs uppercase tracking-wide text-muted-foreground">
          Unit
        </span>
        <div className="mt-0.5 flex flex-wrap items-center gap-x-2 gap-y-0.5">
          <span className="font-medium">{unitDisplayName}</span>
          <span className="truncate font-mono text-xs text-muted-foreground">
            unit://{unitId}
          </span>
        </div>
      </div>

      <AgentCreateForm
        context="dialog"
        initialUnitIds={[unitId]}
        onSuccess={close}
        onCancel={close}
      />
    </Dialog>
  );
}
