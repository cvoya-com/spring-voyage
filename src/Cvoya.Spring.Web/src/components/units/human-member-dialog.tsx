"use client";

// Add / edit a human team-role membership on a unit (#2270 / #2427).
//
// Add mode (`mode="add"`):
//   - The operator picks a role + free-form expertise + free-form
//     notifications. The Human field is auto-filled with the
//     operator's own Human id and rendered as the disabled "You"
//     row — OSS exposes exactly one human and the backend rejects
//     writes targeting any other humanId.
//   - On submit the dialog POSTs to
//     `POST /api/v1/tenant/units/{id}/members/humans`.
//
// Edit mode (`mode="edit"`):
//   - The dialog is seeded from the existing row. Role + humanId
//     are immutable (PATCH only touches expertise / notifications);
//     the dialog renders them as read-only context.
//   - On submit the dialog PATCHes
//     `PATCH /api/v1/tenant/units/{id}/members/humans/{humanId}/{role}`.
//
// Free-form tag fields (expertise + notifications) accept
// comma-separated input and emit a deduped string[] on submit.

import { useMemo, useState } from "react";

import { Badge } from "@/components/ui/badge";
import { Button } from "@/components/ui/button";
import { Dialog } from "@/components/ui/dialog";
import { Input } from "@/components/ui/input";
import { useHuman } from "@/lib/api/queries";
import type { UnitHumanMemberResponse } from "@/lib/api/types";

export interface HumanMemberFormValues {
  /** Target human id. In add-mode this is the operator's own id. */
  humanId: string;
  role: string;
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
  const [role, setRole] = useState(() =>
    mode === "edit" && initial ? initial.role : "",
  );
  const [expertiseText, setExpertiseText] = useState(() =>
    mode === "edit" && initial ? initial.expertise.join(", ") : "",
  );
  const [notificationsText, setNotificationsText] = useState(() =>
    mode === "edit" && initial ? initial.notifications.join(", ") : "",
  );
  const [error, setError] = useState<string | null>(null);

  if (open && seedKey !== lastSeedKey) {
    setLastSeedKey(seedKey);
    if (mode === "edit" && initial) {
      setRole(initial.role);
      setExpertiseText(initial.expertise.join(", "));
      setNotificationsText(initial.notifications.join(", "));
    } else {
      setRole("");
      setExpertiseText("");
      setNotificationsText("");
    }
    setError(null);
  }

  const trimmedRole = role.trim();
  const expertiseList = useMemo(
    () => parseTagInput(expertiseText),
    [expertiseText],
  );
  const notificationsList = useMemo(
    () => parseTagInput(notificationsText),
    [notificationsText],
  );

  const submitDisabled =
    pending ||
    !humanId ||
    (mode === "add" && trimmedRole.length === 0);

  const handleSubmit = async () => {
    if (mode === "add" && trimmedRole.length === 0) {
      setError("Role is required.");
      return;
    }
    if (!humanId) {
      setError("No human identity available — try again in a moment.");
      return;
    }
    setError(null);
    await onSubmit({
      humanId,
      role: mode === "edit" && initial ? initial.role : trimmedRole,
      expertise: expertiseList,
      notifications: notificationsList,
    });
  };

  const title = mode === "add" ? "Add human member" : "Edit human member";
  const description =
    mode === "add"
      ? "Add yourself to this unit with a team role and optional expertise / notifications."
      : "Update this membership's expertise and notification tags. The team role is immutable — remove and re-add to change it.";

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

        <Field label="Role">
          {mode === "edit" && initial ? (
            <div
              className="rounded-md border border-input bg-muted/30 px-3 py-2 text-sm"
              data-testid="human-member-dialog-role-readonly"
            >
              {initial.role}
            </div>
          ) : (
            <Input
              type="text"
              value={role}
              onChange={(e) => setRole(e.target.value)}
              placeholder="e.g. tech-lead, reviewer, on-call"
              required
              data-testid="human-member-dialog-role"
              disabled={pending}
            />
          )}
        </Field>

        <Field label="Expertise (comma-separated)">
          <Input
            type="text"
            value={expertiseText}
            onChange={(e) => setExpertiseText(e.target.value)}
            placeholder="e.g. databases, security, frontend"
            data-testid="human-member-dialog-expertise"
            disabled={pending}
          />
          {expertiseList.length > 0 && (
            <PreviewChips
              items={expertiseList}
              testId="human-member-dialog-expertise-preview"
            />
          )}
        </Field>

        <Field label="Notifications (comma-separated)">
          <Input
            type="text"
            value={notificationsText}
            onChange={(e) => setNotificationsText(e.target.value)}
            placeholder="e.g. pull-requests, incidents"
            data-testid="human-member-dialog-notifications"
            disabled={pending}
          />
          {notificationsList.length > 0 && (
            <PreviewChips
              items={notificationsList}
              testId="human-member-dialog-notifications-preview"
            />
          )}
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
    <label className="block space-y-1.5">
      <span className="text-sm font-medium">{label}</span>
      {children}
    </label>
  );
}

function PreviewChips({
  items,
  testId,
}: {
  items: readonly string[];
  testId: string;
}) {
  return (
    <div
      className="mt-1.5 flex flex-wrap gap-1"
      data-testid={testId}
    >
      {items.map((item, i) => (
        <Badge key={`${item}-${i}`} variant="outline" className="text-xs">
          {item}
        </Badge>
      ))}
    </div>
  );
}

/**
 * Split a comma-separated tag string into a clean, deduped list.
 * Whitespace-only tokens are dropped; preserves first-occurrence order.
 */
function parseTagInput(raw: string): string[] {
  const seen = new Set<string>();
  const out: string[] = [];
  for (const part of raw.split(",")) {
    const trimmed = part.trim();
    if (trimmed.length === 0) continue;
    if (seen.has(trimmed)) continue;
    seen.add(trimmed);
    out.push(trimmed);
  }
  return out;
}
