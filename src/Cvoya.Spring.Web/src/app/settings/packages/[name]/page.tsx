/**
 * /settings/packages/[name] — moved from `/packages/[name]` (#864 / SET-packages).
 *
 * Post-`DEL-packages-top` (#874) the implementation lives at
 * `@/components/admin/package-detail-client`. The server-component
 * page awaits `params` and forwards the name to the client.
 */

import PackageDetailClient from "@/components/admin/package-detail-client";

interface PageProps {
  params: Promise<{ name: string }>;
}

export default async function PackageDetailPage({ params }: PageProps) {
  const { name } = await params;
  return <PackageDetailClient name={name} />;
}
