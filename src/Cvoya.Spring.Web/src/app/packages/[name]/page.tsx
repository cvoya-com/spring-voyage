import PackageDetailClient from "./package-detail-client";

interface PageProps {
  params: Promise<{ name: string }>;
}

export default async function PackageDetailPage({ params }: PageProps) {
  const { name } = await params;
  return <PackageDetailClient name={name} />;
}
