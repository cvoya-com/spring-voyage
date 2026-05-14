"use client";

// Unified Memory tab (canonical-tabs.md § 5.4, #2257).
//
// Renders the same body for Unit and Agent subjects — the two surfaces
// were byte-for-byte duplicates aside from the `useMemories(scope, id)`
// argument. The canonical control accepts `{ kind, id }` and routes the
// scope discriminator through to the hook.
//
// Tenant is intentionally not in the prop union: Tenant does not have
// memory (§ 1 principle / § 4.1 of canonical-tabs.md). The pre-existing
// `tenant-memory.tsx` placeholder is removed under this design.
//
// In v0.1 both short-term + long-term lists always come back empty —
// the real write API + backing store ship in V21-memory-write per plan
// §13. The component renders the empty-state referencing v2.1 so
// reviewers can verify the wiring. If the response is ever non-empty
// the entries render as a simple `<ul>`.

import { Brain } from "lucide-react";

import { ApiErrorMessage } from "@/components/ui/api-error-message";
import { useMemories } from "@/lib/api/queries";
import { timeAgo } from "@/lib/utils";

import type { MemoryEntry } from "@/lib/api/types";

export type MemorySubjectKind = "Unit" | "Agent";

export interface MemoryTabProps {
  /** Subject kind — drives the memory-scope filter on the API hook. */
  kind: MemorySubjectKind;
  /** Stable id of the subject (unit id or agent id). */
  id: string;
}

// Maps the subject kind to the API hook's scope discriminator. Kept
// inline rather than promoted to a util because it is the only place
// the kind ⇄ scope correspondence is load-bearing.
const SCOPE: Record<MemorySubjectKind, "unit" | "agent"> = {
  Unit: "unit",
  Agent: "agent",
};

export function MemoryTab({ kind, id }: MemoryTabProps) {
  const scope = SCOPE[kind];
  const testIdRoot = `tab-${scope}-memory`;
  const { data, isLoading, error } = useMemories(scope, id);

  if (isLoading) {
    return (
      <p
        role="status"
        aria-live="polite"
        className="text-sm text-muted-foreground"
        data-testid={`${testIdRoot}-loading`}
      >
        Loading memory entries…
      </p>
    );
  }

  if (error) {
    return (
      <div data-testid={`${testIdRoot}-error`}>
        <ApiErrorMessage error={error} />
      </div>
    );
  }

  const shortTerm: MemoryEntry[] = data?.shortTerm ?? [];
  const longTerm: MemoryEntry[] = data?.longTerm ?? [];
  const total = shortTerm.length + longTerm.length;

  if (total === 0) {
    return (
      <div
        data-testid={`${testIdRoot}-empty`}
        className="rounded-lg border border-dashed border-border bg-muted/30 p-6 text-center"
      >
        <Brain className="mx-auto h-6 w-6 text-muted-foreground" aria-hidden="true" />
        <p className="mt-2 text-sm font-medium">No memory entries yet</p>
        <p className="mt-1 text-xs text-muted-foreground">
          Write API ships in v2.1 (V21-memory-write).
        </p>
      </div>
    );
  }

  return (
    <div className="space-y-4" data-testid={testIdRoot}>
      <MemoryList title="Short-term" entries={shortTerm} />
      <MemoryList title="Long-term" entries={longTerm} />
    </div>
  );
}

function MemoryList({
  title,
  entries,
}: {
  title: string;
  entries: MemoryEntry[];
}) {
  if (entries.length === 0) return null;
  return (
    <section className="space-y-2">
      <h3 className="text-sm font-medium">{title}</h3>
      <ul className="divide-y divide-border rounded-md border border-border text-sm">
        {entries.map((entry) => (
          <li key={entry.id} className="space-y-1 px-3 py-2">
            <p className="whitespace-pre-wrap">{entry.content}</p>
            <div className="flex items-center gap-2 text-xs text-muted-foreground">
              <span>{timeAgo(entry.createdAt as unknown as string)}</span>
              {entry.source ? <span>· {entry.source}</span> : null}
            </div>
          </li>
        ))}
      </ul>
    </section>
  );
}
