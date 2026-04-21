/**
 * `/units/[name]` — unit detail scaffold (T-06, issue #948).
 *
 * This route is deliberately minimal in T-06: name, status badge,
 * description, runtime, image, model plus a breadcrumb back to
 * `/units`. The Validation panel (T-07), inline remediation, and retry
 * affordances are layered on top of this scaffold by subsequent PRs.
 *
 * The page is split into a server component (this file) and a client
 * component (`UnitDetailClient`) — the client half owns the TanStack
 * Query fetch and the SSE subscription (`useActivityStream`), which
 * invalidates the unit's cache on `StateChanged` / `ValidationProgress`
 * events scoped to this unit so T-07 picks up live progress without a
 * further wiring PR.
 *
 * Next.js 16 App Router treats `params` as a Promise — the page awaits
 * it before handing the decoded `name` to the client component. Mirrors
 * the pattern already used by `src/app/settings/packages/[name]/page.tsx`.
 */

import UnitDetailClient from "@/components/units/detail/unit-detail-client";

interface PageProps {
  params: Promise<{ name: string }>;
}

export default async function UnitDetailPage({ params }: PageProps) {
  const { name } = await params;
  return <UnitDetailClient name={name} />;
}
