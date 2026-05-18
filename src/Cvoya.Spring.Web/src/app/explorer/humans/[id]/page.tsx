/**
 * `/explorer/humans/[id]` — canonical human entity entry point (#2473).
 *
 * Humans have a dedicated detail route (`/humans/[id]`) that already
 * accepts both dashed and no-dash UUIDs. This route simply redirects
 * there so the canonical `/explorer/humans/<id>` URL works.
 */

import { redirect } from "next/navigation";

interface PageProps {
  params: Promise<{ id: string }>;
}

export default async function ExplorerHumansRedirect({
  params,
}: PageProps): Promise<never> {
  const { id } = await params;
  redirect(`/humans/${encodeURIComponent(id)}`);
}
