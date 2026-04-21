"use client";

/**
 * Client half of the `/units/[name]` scaffold (T-06, issue #948).
 *
 * Responsibilities in this PR:
 *   - Fetch the unit envelope + its execution defaults (image / runtime)
 *     through the shared TanStack Query hooks so cache keys line up with
 *     `useActivityStream`'s invalidation map.
 *   - Render the scaffold facts (name, status badge, description,
 *     runtime, image, model) plus a breadcrumb back to `/units`.
 *   - Subscribe to the activity SSE stream filtered to this unit and the
 *     two event types the backend will emit for validation: `StateChanged`
 *     (already shipping) and `ValidationProgress` (landing in T-05). The
 *     filter short-circuits anything else so unrelated events do not
 *     thrash this page's caches.
 *
 * Deliberately NOT in scope (deferred to T-07):
 *   - Validation panel rendering `LastValidationErrorJson`.
 *   - Retry / revalidate button.
 *   - Inline credential edit.
 *
 * Status badge: reuses the variant palette introduced by `UnitCard`
 * (`src/components/cards/unit-card.tsx`) inline — no shared status-badge
 * component exists yet, and introducing one as a side-effect of T-06
 * would violate scope discipline. The call-site comment points at the
 * eventual extraction if/when a second detail surface needs it.
 */

import Link from "next/link";

import { Breadcrumbs } from "@/components/breadcrumbs";
import { Badge } from "@/components/ui/badge";
import { Card, CardContent, CardHeader, CardTitle } from "@/components/ui/card";
import { Skeleton } from "@/components/ui/skeleton";
import { useUnit, useUnitExecution } from "@/lib/api/queries";
import { useActivityStream } from "@/lib/stream/use-activity-stream";
import type { UnitStatus } from "@/lib/api/types";
import { cn } from "@/lib/utils";

interface Props {
  name: string;
}

// Mirrors `UnitCard`'s variant map so the badge palette stays consistent
// across the dashboard cards and this detail scaffold. If a third
// surface needs these, lift them into a shared `<UnitStatusBadge>` — see
// the scope note at the top of this file.
const statusVariant: Record<
  string,
  "default" | "success" | "warning" | "destructive" | "secondary" | "outline"
> = {
  Draft: "outline",
  Stopped: "secondary",
  Starting: "default",
  Running: "success",
  Stopping: "warning",
  Error: "destructive",
};

const statusDot: Record<string, string> = {
  Draft: "bg-muted-foreground",
  Stopped: "bg-muted-foreground",
  Starting: "bg-yellow-500",
  Running: "bg-green-500",
  Stopping: "bg-yellow-500",
  Error: "bg-red-500",
};

/**
 * Event types this page cares about. `StateChanged` is already on the
 * wire; `ValidationProgress` will start flowing when T-05 emits it.
 * Keeping both in the filter now means T-07 becomes a pure additive
 * rendering change — no stream wiring follow-up.
 *
 * NOTE: the server-side `ActivityEventType` union does not yet include
 * `"ValidationProgress"` (it lands with T-02 / T-05). The filter
 * compares the raw string so an untyped future addition does not need a
 * client re-deploy; the matcher is written in terms of `eventType`
 * values rather than the enum literal so no `as` cast leaks here.
 */
const VALIDATION_EVENT_TYPES: ReadonlySet<string> = new Set([
  "StateChanged",
  "ValidationProgress",
]);

export default function UnitDetailClient({ name }: Props) {
  // Primary unit read — status, displayName, description, model, tool
  // (the launcher, surfaced as "Runtime" in the UI).
  const unitQuery = useUnit(name);

  // Execution defaults carry `image` + `runtime` (the container runtime:
  // docker / podman). Neither sits on the `UnitResponse` envelope, so
  // the detail page pulls the execution block alongside. The endpoint
  // always returns the empty shape when never-set, so callers don't
  // need to branch on 404.
  const executionQuery = useUnitExecution(name);

  // SSE subscription — filtered to this unit + the two validation-
  // relevant event types. The hook itself walks
  // `queryKeysAffectedBySource` on every matching event, which already
  // includes `queryKeys.units.detail(source.path)` for `unit://…`
  // sources. That's exactly the slice `useUnit` above reads from, so
  // the cache refetches automatically when the backend announces a new
  // status or validation step. The unit's id on the Address wire is
  // `Address.path`; `Address.scheme === "unit"` narrows source to this
  // surface. See `src/lib/api/query-keys.ts:216`.
  useActivityStream({
    filter: (event) =>
      event.source.scheme === "unit" &&
      event.source.path === name &&
      VALIDATION_EVENT_TYPES.has(event.eventType),
  });

  const unit = unitQuery.data;
  const execution = executionQuery.data;

  if (unitQuery.isPending) {
    return (
      <div className="space-y-4" data-testid="unit-detail-loading">
        <Breadcrumbs items={[{ label: "Units", href: "/units" }, { label: name }]} />
        <Skeleton className="h-8 w-64" />
        <Skeleton className="h-40" />
      </div>
    );
  }

  if (unitQuery.error) {
    return (
      <div className="space-y-4">
        <Breadcrumbs items={[{ label: "Units", href: "/units" }, { label: name }]} />
        <Card>
          <CardContent className="p-6">
            <p className="text-sm text-destructive" role="alert" data-testid="unit-detail-error">
              Failed to load unit: {unitQuery.error.message}
            </p>
          </CardContent>
        </Card>
      </div>
    );
  }

  if (!unit) {
    return (
      <div className="space-y-4">
        <Breadcrumbs items={[{ label: "Units", href: "/units" }, { label: name }]} />
        <Card>
          <CardContent className="p-6">
            <p className="text-sm text-muted-foreground" data-testid="unit-detail-not-found">
              Unit &quot;{name}&quot; not found.
            </p>
          </CardContent>
        </Card>
      </div>
    );
  }

  const status: UnitStatus | string = unit.status ?? "Draft";
  // `tool` is the launcher / runtime-tool (e.g. `claude-code`). The
  // execution block's `runtime` names the container runtime itself
  // (docker / podman). Render both so operators can see the full
  // dispatch picture; the facts table also surfaces image and model.
  const tool = unit.tool ?? null;
  const runtime = execution?.runtime ?? null;
  const image = execution?.image ?? null;
  const model = unit.model ?? execution?.model ?? null;

  return (
    <div className="space-y-6" data-testid="unit-detail">
      <Breadcrumbs
        items={[
          { label: "Units", href: "/units" },
          { label: unit.displayName ?? unit.name },
        ]}
      />

      <header className="flex flex-wrap items-start justify-between gap-3">
        <div className="min-w-0 flex-1">
          <div className="flex items-center gap-2">
            <span
              aria-hidden="true"
              data-testid="unit-detail-status-dot"
              className={cn(
                "inline-block h-2.5 w-2.5 shrink-0 rounded-full",
                statusDot[status] ?? "bg-muted-foreground",
              )}
            />
            <h1 className="truncate text-2xl font-bold">
              {unit.displayName ?? unit.name}
            </h1>
            <Badge
              variant={statusVariant[status] ?? "outline"}
              data-testid="unit-detail-status"
            >
              {status}
            </Badge>
          </div>
          <p className="mt-1 text-xs text-muted-foreground">
            <Link
              href="/units"
              className="underline-offset-2 hover:underline"
              data-testid="unit-detail-back-link"
            >
              /units
            </Link>
            <span className="mx-1">/</span>
            <span data-testid="unit-detail-name">{unit.name}</span>
          </p>
        </div>
      </header>

      <Card>
        <CardHeader>
          <CardTitle className="text-base">Overview</CardTitle>
        </CardHeader>
        <CardContent className="space-y-4 text-sm">
          {unit.description ? (
            <p
              className="text-sm text-muted-foreground"
              data-testid="unit-detail-description"
            >
              {unit.description}
            </p>
          ) : (
            <p
              className="text-sm italic text-muted-foreground"
              data-testid="unit-detail-description-empty"
            >
              No description.
            </p>
          )}

          <dl className="grid grid-cols-1 gap-3 sm:grid-cols-2">
            <Fact label="Runtime" value={runtime ?? tool} testId="unit-detail-runtime" />
            <Fact label="Image" value={image} testId="unit-detail-image" />
            <Fact label="Model" value={model} testId="unit-detail-model" />
            <Fact label="Launcher" value={tool} testId="unit-detail-tool" />
          </dl>
        </CardContent>
      </Card>
    </div>
  );
}

function Fact({
  label,
  value,
  testId,
}: {
  label: string;
  value: string | null;
  testId: string;
}) {
  return (
    <div className="flex flex-col gap-0.5">
      <dt className="text-xs uppercase tracking-wide text-muted-foreground">
        {label}
      </dt>
      <dd className="text-sm" data-testid={testId}>
        {value ?? (
          <span className="italic text-muted-foreground">(unset)</span>
        )}
      </dd>
    </div>
  );
}

