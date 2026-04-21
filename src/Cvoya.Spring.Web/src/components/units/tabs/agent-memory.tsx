"use client";

// Agent Memory tab (EXP-tab-agent-memory, umbrella #815 §4 / §12).
//
// Reads /api/v1/agents/{id}/memories via `useMemories("agent", id)`.
// Same v2.0 empty-state semantics as the unit-side Memory tab — both
// lists always come back empty until V21-memory-write lands.

import { Brain } from "lucide-react";

import { useMemories } from "@/lib/api/queries";
import { timeAgo } from "@/lib/utils";

import type { MemoryEntry } from "@/lib/api/types";

import { registerTab, type TabContentProps } from "./index";

function AgentMemoryTab({ node }: TabContentProps) {
  const { data, isLoading, error } = useMemories("agent", node.id);
  if (node.kind !== "Agent") return null;

  if (isLoading) {
    return (
      <p
        role="status"
        aria-live="polite"
        className="text-sm text-muted-foreground"
        data-testid="tab-agent-memory-loading"
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
        data-testid="tab-agent-memory-error"
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
        data-testid="tab-agent-memory-empty"
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
    <div className="space-y-4" data-testid="tab-agent-memory">
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

registerTab("Agent", "Memory", AgentMemoryTab);

export default AgentMemoryTab;
