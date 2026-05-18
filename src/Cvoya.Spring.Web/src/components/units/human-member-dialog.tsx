"use client";

// Add / edit a human team-role membership on a unit (ADR-0046 Phase 4).
//
// ADR-0046 collapsed the previous role-per-row shape into a single row
// per (unit, human) carrying `roles: string[]` plus `expertise` /
// `notifications`. The dialog now drives all three through the shared
// `<TagChipEditor>`:
//
//   - Roles      → `variant="row"` (chips wrap on multiple lines).
//   - Expertise  → `variant="stack"` (one chip per line — expertise
//                  values are often long).
//   - Notifications → `variant="stack"` (same long-string rationale).
//
// Add mode (`mode="add"`):
//   - The Human field is auto-filled with the operator's own Human id
//     and rendered as the disabled "You" row — OSS exposes exactly one
//     human and the backend rejects writes targeting any other humanId.
//   - On submit the dialog POSTs to
//     `POST /api/v1/tenant/units/{id}/members/humans`.
//
// Edit mode (`mode="edit"`):
//   - The dialog is seeded from the existing row. humanId is immutable;
//     roles, expertise, and notifications are all editable through
//     their respective TagChipEditors.
//   - On submit the dialog PATCHes
//     `PATCH /api/v1/tenant/units/{id}/members/humans/{humanId}`.

import { useState } from "react";

import { Badge } from "@/components/ui/badge";
import { Button } from "@/components/ui/button";
import { Dialog } from "@/components/ui/dialog";
import { TagChipEditor } from "@/components/ui/tag-chip-editor";
import { useHuman } from "@/lib/api/queries";
import type { UnitHumanMemberResponse } from "@/lib/api/types";

export interface HumanMemberFormValues {
  /** Target human id. In add-mode this is the operator's own id. */
  humanId: string;
  roles: string[];
  expertise: string[];
  notifications: string[];
}

interface HumanMemberDialogProps {
  open: boolean;
  mode: "add" | "edit";
  /**
   * The existing membership row to seed the form from. Required when
   * `mode === "edit"` — null in add-mode (the dialog seeds itself
   * from `operatorHumanId`).
   */
  initial: UnitHumanMemberResponse | null;
  /**
   * The currently-authenticated operator's Human id. Used as the
   * auto-filled humanId in add-mode. Null while `/auth/me` is loading;
   * the parent disables the Add button until a value is available.
   */
  operatorHumanId: string | null;
  pending: boolean;
  onCancel: () => void;
  onSubmit: (values: HumanMemberFormValues) => Promise<void> | void;
}

export function HumanMemberDialog({
  open,
  mode,
  initial,
  operatorHumanId,
  pending,
  onCancel,
  onSubmit,
}: HumanMemberDialogProps) {
  // In add-mode we seed the humanId from the operator; in edit-mode
  // from the initial row. Either way the id is immutable in the form.
  const humanId =
    mode === "edit" ? initial?.humanId ?? "" : operatorHumanId ?? "";
  const humanQuery = useHuman(humanId, { enabled: open && Boolean(humanId) });
  const humanDisplayName =
    humanQuery.data?.displayName ?? humanQuery.data?.username ?? humanId;

  // Form state. The seed values come from the (mode, initial) pair —
  // when the dialog re-opens against a different row, we reset via the
  // React "adjust state while rendering" pattern (a previous-value
  // comparison + setState during render). That avoids the
  // `react-hooks/set-state-in-effect` rule, since a useEffect that
  // calls setState would force an extra render every time the dialog
  // opens.
  const seedKey = open
    ? `${mode}:${initial?.membershipId ?? "add"}`
    : "closed";
  const [lastSeedKey, setLastSeedKey] = useState(seedKey);
  const [roles, setRoles] = useState<string[]>(() =>
    mode === "edit" && initial ? [...initial.roles] : [],
  );
  const [expertise, setExpertise] = useState<string[]>(() =>
    mode === "edit" && initial ? [...initial.expertise] : [],
  );
  const [notifications, setNotifications] = useState<string[]>(() =>
    mode === "edit" && initial ? [...initial.notifications] : [],
  );
  const [error, setError] = useState<string | null>(null);

  if (open && seedKey !== lastSeedKey) {
    setLastSeedKey(seedKey);
    if (mode === "edit" && initial) {
      setRoles([...initial.roles]);
      setExpertise([...initial.expertise]);
      setNotifications([...initial.notifications]);
    } else {
      setRoles([]);
      setExpertise([]);
      setNotifications([]);
    }
    setError(null);
  }

  const submitDisabled = pending || !humanId || roles.length === 0;

  const handleSubmit = async () => {
    if (roles.length === 0) {
      setError("At least one role is required.");
      return;
    }
    if (!humanId) {
      setError("No human identity available — try again in a moment.");
      return;
    }
    setError(null);
    await onSubmit({
      humanId,
      roles,
      expertise,
      notifications,
    });
  };

  const title = mode === "add" ? "Add human member" : "Edit human member";
  const description =
    mode === "add"
      ? "Add yourself to this unit with one or more team roles and optional expertise / notifications."
      : "Update the team roles, expertise, and notification tags for this membership.";

  return (
    <Dialog
      open={open}
      onClose={onCancel}
      title={title}
      description={description}
      footer={
        <>
          <Button variant="outline" onClick={onCancel} disabled={pending}>
            Cancel
          </Button>
          <Button
            onClick={() => {
              void handleSubmit();
            }}
            disabled={submitDisabled}
            data-testid="human-member-dialog-submit"
          >
            {pending ? "…" : mode === "add" ? "Add" : "Save"}
          </Button>
        </>
      }
    >
      <div className="space-y-4">
        <Field label="Human">
          <div
            className="flex items-center gap-2 rounded-md border border-input bg-muted/30 px-3 py-2 text-sm"
            data-testid="human-member-dialog-human"
          >
            <span className="font-medium">{humanDisplayName}</span>
            <Badge variant="outline" data-testid="human-member-dialog-you-hint">
              You
            </Badge>
          </div>
          <p className="mt-1 text-xs text-muted-foreground">
            OSS surfaces exactly one human (yourself); hosted variants will
            let an operator pick a teammate here.
          </p>
        </Field>

        <Field label="Roles">
          <TagChipEditor
            values={roles}
            onChange={setRoles}
            placeholder="e.g. tech-lead, reviewer, on-call"
            variant="row"
            disabled={pending}
            testId="human-member-dialog-roles"
            aria-label="Roles"
          />
          <p className="mt-1 text-xs text-muted-foreground">
            At least one role is required.
          </p>
        </Field>

        <Field label="Expertise">
          <TagChipEditor
            values={expertise}
            onChange={setExpertise}
            placeholder="e.g. databases, security, frontend"
            variant="stack"
            disabled={pending}
            testId="human-member-dialog-expertise"
            aria-label="Expertise"
          />
        </Field>

        <Field label="Notifications">
          <TagChipEditor
            values={notifications}
            onChange={setNotifications}
            placeholder="e.g. pull-requests, incidents"
            variant="stack"
            disabled={pending}
            testId="human-member-dialog-notifications"
            aria-label="Notifications"
          />
        </Field>

        {error && (
          <p
            className="text-sm text-destructive"
            data-testid="human-member-dialog-error"
          >
            {error}
          </p>
        )}
      </div>
    </Dialog>
  );
}

function Field({
  label,
  children,
}: {
  label: string;
  children: React.ReactNode;
}) {
  return (
    <div className="block space-y-1.5">
      <span className="text-sm font-medium">{label}</span>
      {children}
    </div>
  );
}
