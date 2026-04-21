/**
 * /settings/packages/[name]/templates/[templateName] — moved from
 * `/packages/[name]/templates/[templateName]` (#864 / SET-packages).
 *
 * Post-`DEL-packages-top` (#874) the implementation lives at
 * `@/components/admin/template-detail-client`. The server-component
 * page awaits `params` and forwards the package + template names to
 * the client.
 */

import TemplateDetailClient from "@/components/admin/template-detail-client";

interface PageProps {
  params: Promise<{ name: string; templateName: string }>;
}

export default async function TemplateDetailPage({ params }: PageProps) {
  const { name, templateName } = await params;
  return (
    <TemplateDetailClient packageName={name} templateName={templateName} />
  );
}
