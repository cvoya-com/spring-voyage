import TemplateDetailClient from "./template-detail-client";

interface PageProps {
  params: Promise<{ name: string; templateName: string }>;
}

export default async function TemplateDetailPage({ params }: PageProps) {
  const { name, templateName } = await params;
  return <TemplateDetailClient packageName={name} templateName={templateName} />;
}
