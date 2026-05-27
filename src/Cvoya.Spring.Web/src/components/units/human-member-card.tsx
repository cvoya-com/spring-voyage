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
// The card's primary surface is a click-through link to the human's
// dedicated detail page (`/humans/<guid>`). Edit / Remove affordances
// live on the card and bubble back to the owning Members tab through
// `onEdit` / `onRemove`. The card itself is presentation-only: no
// mutation calls or local state beyond what the parent needs for
// dialog wiring.

import Link from "next/link";
import { useMemo } from "react";
import { useMutation, useQueryClient } from "@tanstack/react-query";
import { HandIcon, Loader2, Pencil, Trash2, UserRound } from "lucide-react";

import { Badge } from "@/components/ui/badge";
import { Button } from "@/components/ui/button";
import { Card, CardContent } from "@/components/ui/card";
import { useToast } from "@/components/ui/toast";
import { api } from "@/lib/api/client";
import { useCallerHumans, useHuman } from "@/lib/api/queries";
import { queryKeys } from "@/lib/api/query-keys";
import { formatTranslatedError } from "@/lib/api/translate-error";
import type {
  HumanResponse,
  UnitHumanMemberResponse,
} from "@/lib/api/types";

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
  /**
   * The currently-authenticated caller's TenantUser id, or `null`
   * while `/auth/me` is loading. Used by the "Claim this Human"
   * affordance (ADR-0062 § 1) to PATCH the Human row's
   * `tenant_user_id` FK so the caller starts receiving messages
   * addressed to this Hat. The button is hidden when the caller
   * already owns the Hat (per `useCallerHumans`).
   */
  operatorTenantUserId: string | null;
  onEdit: () => void;
  onRemove: () => void;
}

export function HumanMemberCard({
  row,
  operatorHumanId,
  operatorTenantUserId,
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

  const href = `/humans/${encodeURIComponent(row.humanId)}`;

  // ADR-0062 § 1: render a "Claim this Human" button when the caller
  // is not yet bound to this Hat. The bound-Hat set comes from
  // `useCallerHumans` and is the authoritative answer to "do I own
  // this Hat?" — `isMe` only catches the JWT-username-resolved Human,
  // not the (potentially many) package-declared placeholder Humans the
  // caller could still claim. Hidden in OSS once the single Hat is
  // claimed; surfaces on cloud + on the initial OSS install before
  // the operator binds the package-declared placeholder.
  const callerHumansQuery = useCallerHumans({
    enabled: operatorTenantUserId !== null,
  });
  const alreadyBoundToCaller = useMemo(() => {
    const hats = callerHumansQuery.data ?? [];
    return hats.some((h) => h.humanId === row.humanId);
  }, [callerHumansQuery.data, row.humanId]);
  const queryClient = useQueryClient();
  const { toast } = useToast();
  const claimMutation = useMutation<HumanResponse, Error, void>({
    mutationFn: async () => {
      if (!operatorTenantUserId) {
        throw new Error("No authenticated TenantUser caller available.");
      }
      return api.updateHumanBinding(row.humanId, {
        tenantUserId: operatorTenantUserId,
      });
    },
    onSuccess: () => {
      // Invalidate every dependent surface: the human envelope, the
      // caller's bound-Hat set (drives the from-selector + chips), and
      // the unit's member list (so any "You" hint flips to reflect
      // the new ownership). Inbox lists also re-read because the
      // routing-target set changed.
      queryClient.invalidateQueries({
        queryKey: queryKeys.humans.detail(row.humanId),
      });
      queryClient.invalidateQueries({
        queryKey: queryKeys.tenantUsers.callerHumans(),
      });
      queryClient.invalidateQueries({ queryKey: queryKeys.units.all });
      queryClient.invalidateQueries({ queryKey: queryKeys.threads.inbox() });
      toast({
        title: `Claimed ${displayName}`,
        description: "You are now bound to this Hat.",
      });
    },
    onError: (err) => {
      toast({
        title: "Claim failed",
        description: formatTranslatedError(err),
        variant: "destructive",
      });
    },
  });
  const canClaim =
    operatorTenantUserId !== null
    && !alreadyBoundToCaller
    && !callerHumansQuery.isLoading;

  return (
    <Card
      data-testid={`unit-human-member-card-${row.membershipId}`}
      className="relative h-full overflow-hidden transition-colors hover:border-primary/50 hover:bg-muted/30 focus-within:ring-2 focus-within:ring-ring focus-within:ring-offset-2"
    >
      <CardContent className="space-y-3 p-4">
        <div className="flex items-start justify-between gap-2">
          <div className="min-w-0 flex-1">
            <Link
              href={href}
              aria-label={`Open human ${displayName}`}
              data-testid={`unit-human-member-link-${row.membershipId}`}
              className="flex items-center gap-2 rounded-sm focus-visible:outline-none after:absolute after:inset-0 after:content-['']"
            >
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
            </Link>
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
          {/*
            Edit / Remove sit above the full-card overlay link via
            `relative z-[1]` + `pointer-events-auto` so their click
            targets are not eaten by the navigation overlay. Mirrors
            the pattern `<UnitCard>` and `<AgentCard>` use for their
            in-footer action buttons.
          */}
          <div className="pointer-events-auto relative z-[1] flex shrink-0 items-center gap-1">
            {canClaim && (
              <Button
                variant="ghost"
                size="icon"
                onClick={() => claimMutation.mutate()}
                disabled={claimMutation.isPending}
                aria-label={`Claim ${displayName} as one of your Hats`}
                title="Claim this Human as one of your Hats"
                data-testid={`unit-human-member-claim-${row.membershipId}`}
                className="h-7 w-7"
              >
                {claimMutation.isPending ? (
                  <Loader2 className="h-3.5 w-3.5 animate-spin" />
                ) : (
                  <HandIcon className="h-3.5 w-3.5" />
                )}
              </Button>
            )}
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
