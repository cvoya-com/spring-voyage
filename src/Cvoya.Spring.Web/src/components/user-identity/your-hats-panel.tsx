"use client";

// "Your Hats" panel for the /settings/user-identity page (ADR-0062 §§ 1, 5).
//
// Lists every `Human` row bound to the calling caller's `TenantUser`
// (`humans.tenant_user_id = caller`), with the primary Hat flagged.
// Read-only for v0.1; repinning the primary Hat ships under
// #2807-followup once `PATCH /api/v1/tenant/users/{id}` learns the
// `primaryHumanId` field (the back-end resolver already honours it).
//
// The panel mirrors the operator's view of the platform's "speaking-as"
// surface: the same set populates the inbox per-Hat chip and the
// composer's from-selector. Surfacing it here completes the loop —
// every Hat the caller can send as is visible from one settings page.

import { UserRound } from "lucide-react";

import { Badge } from "@/components/ui/badge";
import { Card, CardContent } from "@/components/ui/card";
import { Skeleton } from "@/components/ui/skeleton";
import { useCallerHumans } from "@/lib/api/queries";
import {
  formatHumanContext,
  formatHumanLabel,
} from "@/components/conversation/human-from-selector";

export function YourHatsPanel() {
  const hatsQuery = useCallerHumans();

  if (hatsQuery.isLoading) {
    return (
      <div className="space-y-2">
        <Skeleton className="h-16" />
        <Skeleton className="h-16" />
      </div>
    );
  }

  if (hatsQuery.isError) {
    return (
      <Card>
        <CardContent className="px-6 py-4 text-sm text-destructive">
          Could not load your Hats.
        </CardContent>
      </Card>
    );
  }

  const hats = hatsQuery.data ?? [];
  if (hats.length === 0) {
    return (
      <Card>
        <CardContent className="px-6 py-4 text-sm text-muted-foreground">
          You are not yet bound to any Human. Claim a Human on a unit&apos;s
          Members tab to start receiving messages on this account.
        </CardContent>
      </Card>
    );
  }

  return (
    <ul className="space-y-2" data-testid="your-hats-list">
      {hats.map((hat) => {
        const context = formatHumanContext(hat);
        return (
          <li
            key={hat.humanId}
            data-testid={`your-hats-item-${hat.humanId}`}
          >
            <Card>
              <CardContent className="flex items-center gap-3 px-4 py-3">
                <UserRound
                  className="h-4 w-4 shrink-0 text-muted-foreground"
                  aria-hidden="true"
                />
                <div className="flex min-w-0 flex-1 items-center gap-2">
                  <span
                    className="truncate font-medium"
                    data-testid={`your-hats-name-${hat.humanId}`}
                    title={formatHumanLabel(hat)}
                  >
                    {hat.displayName}
                  </span>
                  {context && (
                    <span
                      className="truncate text-xs text-muted-foreground"
                      data-testid={`your-hats-context-${hat.humanId}`}
                    >
                      {context}
                    </span>
                  )}
                </div>
                {hat.isPrimary && (
                  <Badge
                    variant="outline"
                    className="shrink-0"
                    data-testid={`your-hats-primary-${hat.humanId}`}
                  >
                    primary
                  </Badge>
                )}
              </CardContent>
            </Card>
          </li>
        );
      })}
    </ul>
  );
}
