import AgentDetailClient from "./agent-detail-client";

// Static-export build: dynamic segments must be enumerated at build time.
// Agent ids are created at runtime, so emit a placeholder and rely on
// client-side navigation. Same pattern as /units/[id]/page.tsx.
export function generateStaticParams(): { id: string }[] {
  return [{ id: "__placeholder__" }];
}

interface PageProps {
  params: Promise<{ id: string }>;
}

export default async function AgentDetailPage({ params }: PageProps) {
  const { id } = await params;
  return <AgentDetailClient id={id} />;
}
