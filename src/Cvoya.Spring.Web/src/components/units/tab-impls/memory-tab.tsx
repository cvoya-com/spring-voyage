"use client";

// Unified Memory tab (canonical-tabs.md § 5.4, #2257 / #2342).
//
// Renders the same body for Unit and Agent subjects — the two surfaces
// were byte-for-byte duplicates aside from the `useMemories(subject, id)`
// argument. The canonical control accepts `{ kind, id }` and routes the
// subject discriminator through to the hook.
//
// Tenant is intentionally not in the prop union: Tenant does not have
// memory (§ 1 principle / § 4.1 of canonical-tabs.md). The pre-existing
// `tenant-memory.tsx` placeholder is removed under this design.
//
// v0.1 (#2342) — read-only view. Operator-side write affordances (add /
// update / delete) land in v0.2 under #2357.
//
// The component runs offset paging client-side via the GET endpoint's
// `limit` + `offset` query params, and routes the search box through
// the server-side FTS path (`?query=`) so the result set is ordered by
// relevance rather than by createdAt.

import { Brain, Search, X } from "lucide-react";
import { useState } from "react";

import { ApiErrorMessage } from "@/components/ui/api-error-message";
import { Button } from "@/components/ui/button";
import { Input } from "@/components/ui/input";
import { useMemories } from "@/lib/api/queries";
import { cn, timeAgo } from "@/lib/utils";

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

// Per-page size for the list path. Matches the server's default; the
// `Next` button steps offset by this amount.
const PAGE_SIZE = 50;

export function MemoryTab({ kind, id }: MemoryTabProps) {
  const scope = SCOPE[kind];
  const testIdRoot = `tab-${scope}-memory`;

  // `query` is the committed search term — the one we send to the
  // server. `pendingQuery` is the typed-but-not-submitted value
  // bound to the input so the user can type freely without firing a
  // refetch on every keystroke.
  const [query, setQuery] = useState("");
  const [pendingQuery, setPendingQuery] = useState("");
  const [offset, setOffset] = useState(0);

  // Search disables paging (server-side FTS is relevance-ordered, so
  // offset has no stable meaning). We pass `limit + 1` when listing
  // so we can detect whether a next page exists without an extra
  // round-trip.
  const limit = query ? PAGE_SIZE : PAGE_SIZE + 1;

  const { data, isLoading, error } = useMemories(scope, id, {
    limit,
    offset: query ? undefined : offset,
    query: query || undefined,
  });

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

  const agentRaw: MemoryEntry[] = data?.agent ?? [];
  const threadRaw: MemoryEntry[] = data?.thread ?? [];

  // When listing (no query), we requested PAGE_SIZE + 1 entries; if
  // the combined return count exceeds PAGE_SIZE then a next page
  // exists. Trim the overflow entry from whichever scope it landed in
  // before rendering.
  const total = agentRaw.length + threadRaw.length;
  const hasNext = !query && total > PAGE_SIZE;
  const overflowFrom: "agent" | "thread" | null = !hasNext
    ? null
    : agentRaw.length > threadRaw.length
      ? "agent"
      : "thread";
  const agent =
    overflowFrom === "agent" ? agentRaw.slice(0, -1) : agentRaw;
  const thread =
    overflowFrom === "thread" ? threadRaw.slice(0, -1) : threadRaw;

  const visibleTotal = agent.length + thread.length;

  return (
    <div className="space-y-4" data-testid={testIdRoot}>
      <form
        className="flex items-center gap-2"
        role="search"
        onSubmit={(e) => {
          e.preventDefault();
          setQuery(pendingQuery);
          setOffset(0);
        }}
      >
        <div className="relative flex-1">
          <Search
            className="pointer-events-none absolute left-2 top-1/2 h-4 w-4 -translate-y-1/2 text-muted-foreground"
            aria-hidden="true"
          />
          <Input
            type="search"
            placeholder="Search memories…"
            aria-label="Search memories"
            value={pendingQuery}
            onChange={(e) => setPendingQuery(e.target.value)}
            className="pl-8"
            data-testid={`${testIdRoot}-search-input`}
          />
        </div>
        <Button
          type="submit"
          size="sm"
          variant="default"
          data-testid={`${testIdRoot}-search-submit`}
        >
          Search
        </Button>
        {query ? (
          <Button
            type="button"
            size="sm"
            variant="ghost"
            onClick={() => {
              setQuery("");
              setPendingQuery("");
              setOffset(0);
            }}
            data-testid={`${testIdRoot}-search-clear`}
          >
            <X className="mr-1 h-4 w-4" aria-hidden="true" /> Clear
          </Button>
        ) : null}
      </form>

      {visibleTotal === 0 ? (
        <div
          data-testid={`${testIdRoot}-empty`}
          className="rounded-lg border border-dashed border-border bg-muted/30 p-6 text-center"
        >
          <Brain
            className="mx-auto h-6 w-6 text-muted-foreground"
            aria-hidden="true"
          />
          <p className="mt-2 text-sm font-medium">
            {query
              ? `No memory entries match “${query}”.`
              : offset > 0
                ? "No more memory entries on this page."
                : "No memory entries yet."}
          </p>
        </div>
      ) : (
        <>
          <MemorySection
            title="Agent-scoped"
            entries={agent}
            testIdRoot={testIdRoot}
            section="agent"
          />
          <MemorySection
            title="Thread-scoped"
            entries={thread}
            testIdRoot={testIdRoot}
            section="thread"
          />
        </>
      )}

      {/* Pagination — list-mode only. FTS ordering makes offset paging
          meaningless, so the controls hide while a query is active. */}
      {!query ? (
        <div
          className="flex items-center justify-between text-xs text-muted-foreground"
          data-testid={`${testIdRoot}-paging`}
        >
          <span>
            Showing entries {offset + 1}–{offset + visibleTotal}
          </span>
          <div className="flex items-center gap-2">
            <Button
              type="button"
              size="sm"
              variant="outline"
              disabled={offset === 0}
              onClick={() => setOffset(Math.max(0, offset - PAGE_SIZE))}
              data-testid={`${testIdRoot}-paging-prev`}
            >
              Previous
            </Button>
            <Button
              type="button"
              size="sm"
              variant="outline"
              disabled={!hasNext}
              onClick={() => setOffset(offset + PAGE_SIZE)}
              data-testid={`${testIdRoot}-paging-next`}
            >
              Next
            </Button>
          </div>
        </div>
      ) : null}
    </div>
  );
}

interface MemorySectionProps {
  title: string;
  entries: MemoryEntry[];
  testIdRoot: string;
  section: "agent" | "thread";
}

function MemorySection({
  title,
  entries,
  testIdRoot,
  section,
}: MemorySectionProps) {
  const sectionTestId = `${testIdRoot}-${section}-section`;
  const countTestId = `${testIdRoot}-${section}-count`;
  return (
    <section
      className={cn("space-y-2 rounded-md border border-border")}
      data-testid={sectionTestId}
    >
      <header className="flex items-center justify-between border-b border-border px-3 py-2">
        <h3 className="text-sm font-medium">{title}</h3>
        <span
          className="text-xs text-muted-foreground"
          data-testid={countTestId}
        >
          {entries.length} {entries.length === 1 ? "entry" : "entries"}
        </span>
      </header>
      {entries.length === 0 ? (
        <p className="px-3 py-2 text-xs text-muted-foreground">
          No {title.toLowerCase()} entries.
        </p>
      ) : (
        <ul className="divide-y divide-border text-sm">
          {entries.map((entry) => (
            <li
              key={entry.id}
              className="space-y-1 px-3 py-2"
              data-testid={`${testIdRoot}-${section}-item-${entry.id}`}
            >
              {typeof entry.content === "string" ? (
                <p className="whitespace-pre-wrap break-words">
                  {entry.content}
                </p>
              ) : (
                <pre className="overflow-x-auto whitespace-pre-wrap break-words font-mono text-xs">
                  {JSON.stringify(entry.content, null, 2)}
                </pre>
              )}
              <div className="flex flex-wrap items-center gap-x-3 gap-y-1 text-xs text-muted-foreground">
                <span title={String(entry.createdAt)}>
                  {timeAgo(entry.createdAt as unknown as string)}
                </span>
                {entry.updatedAt && entry.updatedAt !== entry.createdAt ? (
                  <span>
                    updated{" "}
                    {timeAgo(entry.updatedAt as unknown as string)}
                  </span>
                ) : null}
                {entry.source ? <span>source: {entry.source}</span> : null}
                {section === "thread" && entry.threadId ? (
                  <span>thread: {String(entry.threadId).slice(0, 8)}</span>
                ) : null}
              </div>
            </li>
          ))}
        </ul>
      )}
    </section>
  );
}
