"use client";

// Unit Memory tab (EXP-tab-unit-memory, umbrella #815 §4 / §12).
//
// Reads /api/v1/units/{id}/memories via `useMemories("unit", id)`. In
// v2.0 both short-term + long-term lists always come back empty — the
// real write API + backing store ship in V21-memory-write per plan §13.
// The tab renders the empty-state referencing v2.1 so reviewers can
// verify the wiring. If the response is ever non-empty (future v2.1
// short-term pre-filled for example) the entry list renders as a
// simple `<ul>`.

import { Brain } from "lucide-react";

import { useMemories } from "@/lib/api/queries";
import { timeAgo } from "@/lib/utils";

import type { MemoryEntry } from "@/lib/api/types";

import { registerTab, type TabContentProps } from "./index";

function UnitMemoryTab({ node }: TabContentProps) {
  // Hook call happens unconditionally so react-hooks/rules-of-hooks is
  // satisfied; the kind narrowing below is a belt-and-braces runtime
  // guard — the registry dispatch guarantees `kind === "Unit"` here.
  const { data, isLoading, error } = useMemories("unit", node.id);
  if (node.kind !== "Unit") return null;

  if (isLoading) {
    return (
      <p
        role="status"
        aria-live="polite"
        className="text-sm text-muted-foreground"
        data-testid="tab-unit-memory-loading"
      >
        Loading memory entries…
      </p>
    );
  }

  if (error) {
    return (
      <p
        role="alert"
        className="rounded-md border border-destructive/50 bg-destructive/10 px-3 py-2 text-sm text-destructive"
        data-testid="tab-unit-memory-error"
      >
        Couldn&apos;t load memory entries:{" "}
        {error instanceof Error ? error.message : String(error)}
      </p>
    );
  }

  const shortTerm: MemoryEntry[] = data?.shortTerm ?? [];
  const longTerm: MemoryEntry[] = data?.longTerm ?? [];
  const total = shortTerm.length + longTerm.length;

  if (total === 0) {
    return (
      <div
        data-testid="tab-unit-memory-empty"
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
    <div className="space-y-4" data-testid="tab-unit-memory">
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

registerTab("Unit", "Memory", UnitMemoryTab);

export default UnitMemoryTab;
