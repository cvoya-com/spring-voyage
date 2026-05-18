"use client";

// Canonical Explorer entry point at `/explorer/units/<id>` (#2473). The
// `[id]` segment is a no-dash UUID; the shared `<ExplorerSurface>` reads
// it from the URL pathname snapshot so within-Explorer tree clicks
// (which use `window.history.replaceState`, not router.replace) flow
// through immediately without an App Router round-trip.

import { Suspense } from "react";
import { Loader2 } from "lucide-react";

import { ExplorerSurface } from "@/components/units/explorer-surface";

export default function ExplorerUnitsPage() {
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
      <ExplorerSurface />
    </Suspense>
  );
}
