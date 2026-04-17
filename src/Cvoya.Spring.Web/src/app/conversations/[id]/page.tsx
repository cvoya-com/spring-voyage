import { ConversationDetailClient } from "./conversation-detail-client";

interface PageProps {
  params: Promise<{ id: string }>;
}

export default async function ConversationDetailPage({ params }: PageProps) {
  const { id } = await params;
  return <ConversationDetailClient id={decodeURIComponent(id)} />;
}
