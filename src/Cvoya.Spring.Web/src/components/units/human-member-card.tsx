"use client";

// Human team-role member card (ADR-0046 Phase 4). One card per
// (unit, human) pair — a human now holds a *set* of roles per unit
// (ADR-0046 collapsed the previous role-per-row shape into a single
// row carrying `roles: string[]`).
//
// The card renders the human's display name (fetched via the canonical
// `useHuman` query — the membership row only carries the id), the set
// of team roles as wrapping chips, and `expertise` / `notifications`
// as flat chip lists. Operator-facing identity disambiguation: when the
// row's `humanId` matches the currently-authenticated caller, an
// outlined "You" badge sits next to the display name — same convention
// as the Human × Overview tab established under #2267.
//
// Edit / Remove affordances live on the card and bubble back to the
// owning Members tab through `onEdit` / `onRemove`. The card itself is
// presentation-only: no mutation calls or local state beyond what the
// parent needs for dialog wiring.

import { Pencil, Trash2, UserRound } from "lucide-react";

import { Badge } from "@/components/ui/badge";
import { Button } from "@/components/ui/button";
import { Card, CardContent } from "@/components/ui/card";
import { useHuman } from "@/lib/api/queries";
import type { UnitHumanMemberResponse } from "@/lib/api/types";

interface HumanMemberCardProps {
  row: UnitHumanMemberResponse;
  /**
   * The currently-authenticated caller's Human id, or `null` while
   * `/auth/me` is loading. When the row's `humanId` matches, the
   * card paints the "You" hint to disambiguate the operator's own
   * row from teammates' (only relevant in hosted; in OSS there's
   * exactly one human).
   */
  operatorHumanId: string | null;
  onEdit: () => void;
  onRemove: () => void;
}

export function HumanMemberCard({
  row,
  operatorHumanId,
  onEdit,
  onRemove,
}: HumanMemberCardProps) {
  // Display name lookup. The membership row only carries the id;
  // pulling the human envelope here keeps the cache slot consistent
  // with `/humans/<guid>` and the Detail Pane's Human × Overview
  // tab, so one round-trip serves multiple surfaces.
  const humanQuery = useHuman(row.humanId);
  const displayName =
    humanQuery.data?.displayName ?? humanQuery.data?.username ?? row.humanId;
  const isMe =
    operatorHumanId !== null && operatorHumanId === row.humanId;

  return (
    <Card
      data-testid={`unit-human-member-card-${row.membershipId}`}
      className="h-full"
    >
      <CardContent className="space-y-3 p-4">
        <div className="flex items-start justify-between gap-2">
          <div className="min-w-0 flex-1">
            <div className="flex items-center gap-2">
              <UserRound
                className="h-4 w-4 shrink-0 text-muted-foreground"
                aria-hidden="true"
              />
              <h3
                className="truncate font-semibold"
                data-testid={`unit-human-member-name-${row.membershipId}`}
              >
                {displayName}
              </h3>
              {isMe ? (
                <Badge
                  variant="outline"
                  data-testid={`unit-human-member-you-hint-${row.membershipId}`}
                >
                  You
                </Badge>
              ) : null}
            </div>
            {row.roles.length > 0 && (
              <div
                className="mt-1.5 flex flex-wrap items-center gap-1.5"
                data-testid={`unit-human-member-roles-${row.membershipId}`}
              >
                {row.roles.map((roleValue, index) => (
                  <Badge
                    key={`${roleValue}-${index}`}
                    variant="secondary"
                    data-testid={`unit-human-member-role-${row.membershipId}-${index}`}
                  >
                    {roleValue}
                  </Badge>
                ))}
              </div>
            )}
          </div>
          <div className="flex shrink-0 items-center gap-1">
            <Button
              variant="ghost"
              size="icon"
              onClick={onEdit}
              aria-label={`Edit roles for ${displayName}`}
              data-testid={`unit-human-member-edit-${row.membershipId}`}
              className="h-7 w-7"
            >
              <Pencil className="h-3.5 w-3.5" />
            </Button>
            <Button
              variant="ghost"
              size="icon"
              onClick={onRemove}
              aria-label={`Remove ${displayName} from this unit`}
              data-testid={`unit-human-member-remove-${row.membershipId}`}
              className="h-7 w-7"
            >
              <Trash2 className="h-3.5 w-3.5 text-destructive" />
            </Button>
          </div>
        </div>

        {row.expertise.length > 0 && (
          <ChipList
            label="Expertise"
            items={row.expertise}
            testIdPrefix={`unit-human-member-expertise-${row.membershipId}`}
          />
        )}
        {row.notifications.length > 0 && (
          <ChipList
            label="Notifications"
            items={row.notifications}
            testIdPrefix={`unit-human-member-notifications-${row.membershipId}`}
          />
        )}
      </CardContent>
    </Card>
  );
}

function ChipList({
  label,
  items,
  testIdPrefix,
}: {
  label: string;
  items: readonly string[];
  testIdPrefix: string;
}) {
  return (
    <div className="space-y-1">
      <p className="text-xs uppercase tracking-wide text-muted-foreground">
        {label}
      </p>
      <div className="flex flex-wrap gap-1" data-testid={`${testIdPrefix}-list`}>
        {items.map((item, i) => (
          <Badge
            key={`${item}-${i}`}
            variant="outline"
            className="text-xs"
            data-testid={`${testIdPrefix}-${i}`}
          >
            {item}
          </Badge>
        ))}
      </div>
    </div>
  );
}
