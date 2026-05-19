"use client";

// Explorer index route at `/explorer` (#2517). The nav link for Explorer
// points here; renders the Explorer canvas with no node pre-selected so
// users land on the tenant root. Canonical deep-link URLs are
// `/explorer/units/<id>`, `/explorer/agents/<id>`, and
// `/explorer/humans/<id>`; the nav entry path is `/explorer` so the
// browser bar shows `/explorer` rather than `/units` when the user
// clicks the Explorer link in the sidebar.

import { Suspense } from "react";
import { Loader2 } from "lucide-react";

import { ExplorerSurface } from "@/components/units/explorer-surface";

export default function ExplorerIndexPage() {
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
