import ConnectorDetailClient from "./connector-detail-client";

interface PageProps {
  params: Promise<{ type: string }>;
}

export default async function ConnectorDetailPage({ params }: PageProps) {
  const { type } = await params;
  return <ConnectorDetailClient slugOrId={type} />;
}
