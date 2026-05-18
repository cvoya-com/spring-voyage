"use client";

// Issue #2463: edit dialog for the per-membership `roles` + `expertise`
// jsonb columns on the unit ↔ agent and unit ↔ sub-unit edges. This is
// the agent / sub-unit parallel to `<HumanMemberDialog>` for human
// team-role members — both surfaces use the same `<TagChipEditor>`
// primitives so the operator's mental model is uniform across the three
// member kinds (agent / sub-unit / human).
//
// Why a single shared dialog instead of two parallel components:
//
//   - The two rows are wire-symmetric (PATCH semantics, response shape,
//     tri-state list handling all match). The only deltas are the
//     dialog title and the descriptor blurb under the title.
//   - Reusing `<TagChipEditor>` here keeps the chip UX byte-for-byte
//     identical to the human-member side.
//
// Form state seeds from the supplied `initial` arrays whenever the
// dialog re-opens against a different row (the seed-key pattern used in
// `<HumanMemberDialog>` — set state during render rather than a useEffect
// so re-opens don't cost an extra render).

import { useState } from "react";

import { Button } from "@/components/ui/button";
import { Dialog } from "@/components/ui/dialog";
import { TagChipEditor } from "@/components/ui/tag-chip-editor";

/** Form values emitted by the dialog's submit handler. */
export interface MemberMetadataFormValues {
  roles: string[];
  expertise: string[];
}

interface MemberMetadataDialogProps {
  open: boolean;
  /**
   * Edit subject — `agent` flips the title to "Edit agent member" and
   * paints the hint copy accordingly; `sub-unit` is the symmetric form.
   */
  subject: "agent" | "sub-unit";
  /** Display label rendered as a read-only header (e.g. agent's display name). */
  memberLabel: string;
  /** Stable key — the dialog reseeds form state when this value changes. */
  memberKey: string;
  initialRoles: readonly string[];
  initialExpertise: readonly string[];
  pending: boolean;
  onCancel: () => void;
  onSubmit: (values: MemberMetadataFormValues) => Promise<void> | void;
}

/**
 * Edit `roles` + `expertise` for an agent or sub-unit member on a unit.
 * Returns the dialog open / closed; the parent owns the submit handler
 * so the same component drives both PATCH endpoints (#2463).
 */
export function MemberMetadataDialog({
  open,
  subject,
  memberLabel,
  memberKey,
  initialRoles,
  initialExpertise,
  pending,
  onCancel,
  onSubmit,
}: MemberMetadataDialogProps) {
  // Seed-key pattern: when the dialog opens against a different row,
  // reset state during the render pass so re-opens don't pay for an
  // extra render via useEffect. See `<HumanMemberDialog>` for the
  // canonical version of this trick.
  const seedKey = open ? `${subject}:${memberKey}` : "closed";
  const [lastSeedKey, setLastSeedKey] = useState(seedKey);
  const [roles, setRoles] = useState<string[]>(() => [...initialRoles]);
  const [expertise, setExpertise] = useState<string[]>(() => [
    ...initialExpertise,
  ]);

  if (open && seedKey !== lastSeedKey) {
    setLastSeedKey(seedKey);
    setRoles([...initialRoles]);
    setExpertise([...initialExpertise]);
  }

  const handleSubmit = async () => {
    await onSubmit({ roles, expertise });
  };

  const subjectNoun = subject === "agent" ? "agent" : "sub-unit";
  const title = `Edit ${subjectNoun} member`;
  const description = `Update the team roles and expertise tags this ${subjectNoun} advertises on the unit. ${
    subject === "agent"
      ? "Other agent-membership settings (model, specialty, execution mode) are edited from the agent card itself."
      : "The sub-unit's own configuration is edited from its own page; only the per-membership tags are touched here."
  }`;

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
            disabled={pending}
            data-testid="member-metadata-dialog-submit"
          >
            {pending ? "…" : "Save"}
          </Button>
        </>
      }
    >
      <div className="space-y-4">
        <Field label={subject === "agent" ? "Agent" : "Sub-unit"}>
          <div
            className="flex items-center gap-2 rounded-md border border-input bg-muted/30 px-3 py-2 text-sm"
            data-testid="member-metadata-dialog-member"
          >
            <span className="font-medium">{memberLabel}</span>
          </div>
        </Field>

        <Field label="Roles">
          <TagChipEditor
            values={roles}
            onChange={setRoles}
            placeholder="e.g. tech-lead, reviewer, on-call"
            variant="row"
            disabled={pending}
            testId="member-metadata-dialog-roles"
            aria-label="Roles"
          />
        </Field>

        <Field label="Expertise">
          <TagChipEditor
            values={expertise}
            onChange={setExpertise}
            placeholder="e.g. databases, security, frontend"
            variant="stack"
            disabled={pending}
            testId="member-metadata-dialog-expertise"
            aria-label="Expertise"
          />
        </Field>
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
