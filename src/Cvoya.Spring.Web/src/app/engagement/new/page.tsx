// New-engagement form (#1455 / #1456).
//
// URL: /engagement/new[?participant=<scheme>:<path>&...]
//
// The user picks one or more participants (units / agents), types a first
// message, and submits. The form POSTs the seed message to the first
// participant — the API auto-generates a thread id — and then echoes the
// same message to the remaining participants so every selected party shows
// up in the thread's participant list. On success the page navigates to
// `/engagement/{threadId}`.
//
// Pre-population: the page reads `?participant=` query strings and seeds
// them into the picker. The query string is the canonical address form
// (`unit://foo` or `agent://bar`). Multiple `participant` values are
// supported and each appears as a removable chip — wired into the
// management portal's "Start engagement with this unit/agent" affordance
// (#1456).

import type { Metadata } from "next";
import { Suspense } from "react";
import { NewEngagementForm } from "@/components/engagement/new-engagement-form";

export const metadata: Metadata = {
  title: "New engagement — Spring Voyage",
};

export default function NewEngagementPage() {
  return (
    <div className="space-y-6" data-testid="engagement-new-page">
      <div>
        <h1 className="text-2xl font-bold">New engagement</h1>
        <p className="mt-1 text-sm text-muted-foreground">
          Pick the units and agents to engage, write the opening message, and
          send. The thread that materialises is shared with every selected
          participant.
        </p>
      </div>

      {/* The form is a client component — Suspense boundary keeps the
          page render cheap until the first paint. */}
      <Suspense fallback={null}>
        <NewEngagementForm />
      </Suspense>
    </div>
  );
}
