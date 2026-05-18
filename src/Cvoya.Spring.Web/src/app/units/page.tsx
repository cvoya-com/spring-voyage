"use client";

// Legacy Explorer entry point. The canonical Explorer URL is now
// `/explorer/units/<id>` (#2473); this route exists only to translate
// legacy `?node=` bookmarks and to serve the unselected Explorer when
// no node is requested.

import { Suspense, useEffect } from "react";
import { Loader2 } from "lucide-react";
import { useRouter, useSearchParams } from "next/navigation";

import { ExplorerSurface } from "@/components/units/explorer-surface";
import { toExplorerPathSegment } from "@/lib/explorer-url";

function UnitsEntryPoint() {
  const router = useRouter();
  const searchParams = useSearchParams();
  const legacyNode = searchParams.get("node") ?? undefined;
  const tab = searchParams.get("tab") ?? undefined;

  useEffect(() => {
    if (!legacyNode) return;
    const tabPart = tab ? `?tab=${encodeURIComponent(tab)}` : "";
    const humanGuid = parseHumanSelection(legacyNode);
    if (humanGuid !== null) {
      router.replace(`/humans/${encodeURIComponent(humanGuid)}${tabPart}`);
      return;
    }
    const nodePath = toExplorerPathSegment(legacyNode);
    router.replace(`/explorer/units/${encodeURIComponent(nodePath)}${tabPart}`);
  }, [router, legacyNode, tab]);

  // When no legacy `?node=` is set, render the unselected Explorer
  // canvas directly so users who hit `/units` still land on a working
  // surface instead of a transitional spinner.
  if (legacyNode) {
    return (
      <div
        role="status"
        aria-live="polite"
        data-testid="unit-explorer-loading"
        className="flex h-full min-h-[50vh] items-center justify-center text-sm text-muted-foreground"
      >
        <Loader2 className="mr-2 h-4 w-4 animate-spin" aria-hidden="true" />
        Loading tenant tree…
      </div>
    );
  }
  return <ExplorerSurface />;
}

function parseHumanSelection(raw: string): string | null {
  const candidate = raw.startsWith("human://")
    ? raw.slice("human://".length)
    : raw.startsWith("human:")
      ? raw.slice("human:".length)
      : null;
  if (candidate === null) return null;
  const UUID_RE =
    /^[0-9a-f]{8}-?[0-9a-f]{4}-?[0-9a-f]{4}-?[0-9a-f]{4}-?[0-9a-f]{12}$/i;
  if (!UUID_RE.test(candidate)) return null;
  if (candidate.length === 32) {
    return `${candidate.slice(0, 8)}-${candidate.slice(8, 12)}-${candidate.slice(12, 16)}-${candidate.slice(16, 20)}-${candidate.slice(20)}`;
  }
  return candidate;
}

export default function UnitsPage() {
  return (
    <Suspense
      fallback={
        <div
          role="status"
          aria-live="polite"
          data-testid="unit-explorer-loading"
          className="flex h-full min-h-[50vh] items-center justify-center text-sm text-muted-foreground"
        >
          <Loader2 className="mr-2 h-4 w-4 animate-spin" aria-hidden="true" />
          Loading tenant tree…
        </div>
      }
    >
      <UnitsEntryPoint />
    </Suspense>
  );
}
