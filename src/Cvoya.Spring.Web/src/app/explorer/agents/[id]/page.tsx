/**
 * `/explorer/agents/[id]` — canonical agent entry point (#2473).
 *
 * Agents live in the tenant tree alongside units, so this route
 * redirects to `/explorer/units/<id>` — the same Explorer surface
 * that already renders agent detail panes. The `[id]` segment is a
 * no-dash UUID.
 */

import { redirect } from "next/navigation";

interface PageProps {
  params: Promise<{ id: string }>;
}

export default async function ExplorerAgentsRedirect({
  params,
}: PageProps): Promise<never> {
  const { id } = await params;
  redirect(`/explorer/units/${encodeURIComponent(id)}`);
}
