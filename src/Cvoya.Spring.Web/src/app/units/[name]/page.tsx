/**
 * `/units/[name]` — legacy scaffold redirect.
 *
 * The T-06 scaffold (issue #948) was retired by #1011 in favour of the
 * Explorer. This thin server component preserves any bookmarks / external
 * links that still point at the old URL by issuing a `redirect()` to the
 * canonical `/explorer/units/<name>` path. Mirrors the
 * `app/analytics/page.tsx` pattern.
 *
 * #2473: Updated to redirect to the new path-based Explorer URL.
 */

import { redirect } from "next/navigation";

interface PageProps {
  params: Promise<{ name: string }>;
}

export default async function UnitDetailRedirect({
  params,
}: PageProps): Promise<never> {
  const { name } = await params;
  redirect(`/explorer/units/${encodeURIComponent(name)}?tab=Overview`);
}
