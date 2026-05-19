/**
 * `/explorer/tenants/[id]` — stub for future tenant entity detail (#2473).
 *
 * Tenant-level entity detail is not yet implemented in the portal.
 * This stub redirects to the Explorer root so the URL shape is stable
 * without surfacing a blank page.
 */

import { redirect } from "next/navigation";

interface PageProps {
  params: Promise<{ id: string }>;
}

export default async function ExplorerTenantsRedirect(
  _props: PageProps,
): Promise<never> {
  redirect("/explorer");
}
