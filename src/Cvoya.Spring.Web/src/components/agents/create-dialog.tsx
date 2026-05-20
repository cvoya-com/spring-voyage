"use client";

import { useState } from "react";

import { Dialog } from "@/components/ui/dialog";
import {
  AgentCreateForm,
  type AgentCreateSuccess,
} from "@/components/agents/create-form";

// ---------------------------------------------------------------------------
// Props
// ---------------------------------------------------------------------------

export interface AgentCreateDialogProps {
  /**
   * Unit identifier supplied by the caller. The form accepts either the
   * stable Guid (`unit.id`) or the navigation-friendly `unit.name` and
   * resolves it to the direct create API's `unitIds[]` body.
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
  /**
   * Optional — fires with the create result just before the dialog
   * closes on a successful create. Callers that need to act on the new
   * agent (e.g. navigate to its page) wire this; the unit Members tab
   * leaves it unset and simply refreshes its list on close.
   */
  onCreated?: (result: AgentCreateSuccess) => void;
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
 * The dialog defaults to the scratch flow, with a footer text link that
 * can pivot into the from-package picker. The dialog supplies
 * `initialUnitIds` so the unit checkbox is pre-checked, and the strip
 * above the form confirms the assignment so operators do not have to
 * scroll the form to verify it. The form's cancel and success callbacks
 * both close the dialog via `onOpenChange(false)`; the
 * `<AgentCreateForm>` already handles toast / cache invalidation.
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
  onCreated,
}: AgentCreateDialogProps) {
  const [dialogBranch, setDialogBranch] = useState<"scratch" | "from-package">(
    "scratch",
  );
  const close = () => {
    setDialogBranch("scratch");
    onOpenChange(false);
  };

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

      {dialogBranch === "scratch" && (
        <div className="flex justify-start">
          <button
            type="button"
            className="text-xs text-muted-foreground underline-offset-2 hover:text-foreground hover:underline"
            onClick={() => setDialogBranch("from-package")}
            data-testid="agent-create-dialog-from-package-link"
          >
            From package…
          </button>
        </div>
      )}

      <AgentCreateForm
        key={dialogBranch}
        context="dialog"
        initialSource={
          dialogBranch === "from-package" ? "from-package" : undefined
        }
        initialUnitIds={[unitId]}
        onSuccess={(result) => {
          onCreated?.(result);
          close();
        }}
        onCancel={close}
        onSourceBack={() => setDialogBranch("scratch")}
      />
    </Dialog>
  );
}
