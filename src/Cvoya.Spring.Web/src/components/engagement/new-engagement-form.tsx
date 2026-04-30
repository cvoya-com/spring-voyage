"use client";

// New-engagement form (#1455 / #1456).
//
// Lets the operator pick one or more units / agents, write the opening
// message, and submit. We POST the seed message to the first participant
// (auto-generates a threadId) and then echo it to every additional
// participant under the same threadId so they materialise as thread
// participants too. On success, navigate to `/engagement/{threadId}`.
//
// Pre-populated participants flow in via `?participant=<scheme>://<path>`
// query strings (read once on mount). The picker treats them like any
// other selection — they can be removed before submit.

import { useCallback, useMemo, useState } from "react";
import { useRouter, useSearchParams } from "next/navigation";
import {
  Bot,
  Layers,
  Loader2,
  MessagesSquare,
  Plus,
  X,
} from "lucide-react";
import { useMutation } from "@tanstack/react-query";

import { Button } from "@/components/ui/button";
import { Input } from "@/components/ui/input";
import { Card, CardContent, CardHeader, CardTitle } from "@/components/ui/card";
import { Badge } from "@/components/ui/badge";
import { Skeleton } from "@/components/ui/skeleton";
import { useToast } from "@/components/ui/toast";
import { api } from "@/lib/api/client";
import { useTenantTree } from "@/lib/api/queries";
import { cn } from "@/lib/utils";
import type { TenantTreeNode } from "@/lib/api/types";

interface ParticipantRef {
  scheme: "unit" | "agent";
  path: string;
  label: string;
}

const ADDRESS_PATTERN = /^(unit|agent):\/\/(.+)$/;

function parseAddress(raw: string): ParticipantRef | null {
  const m = ADDRESS_PATTERN.exec(raw.trim());
  if (!m) return null;
  return { scheme: m[1] as "unit" | "agent", path: m[2]!, label: m[2]! };
}

function refKey(ref: ParticipantRef): string {
  return ref.scheme + "://" + ref.path;
}

function flattenTree(node: TenantTreeNode): ParticipantRef[] {
  const out: ParticipantRef[] = [];
  function walk(n: TenantTreeNode) {
    const kind = n.kind?.toLowerCase();
    if (kind === "unit") {
      out.push({ scheme: "unit", path: n.id, label: n.name || n.id });
    } else if (kind === "agent") {
      out.push({ scheme: "agent", path: n.id, label: n.name || n.id });
    }
    for (const child of n.children ?? []) walk(child);
  }
  walk(node);
  return out;
}

export function NewEngagementForm() {
  const router = useRouter();
  const searchParams = useSearchParams();
  const { toast } = useToast();
  const treeQuery = useTenantTree();

  // Seed initial selection from `?participant=<scheme>://<path>` query
  // strings via the lazy state initializer so we never have to call
  // `setSelected` from inside an effect (the React 19 lint rule
  // `react-hooks/set-state-in-effect` flags effect-driven setState
  // because cascading renders are a perf/correctness foot-gun).
  const [selected, setSelected] = useState<ParticipantRef[]>(() => {
    if (!searchParams) return [];
    const raw = searchParams.getAll("participant");
    return raw
      .map(parseAddress)
      .filter((r): r is ParticipantRef => r !== null);
  });
  const [body, setBody] = useState("");
  const [filter, setFilter] = useState("");
  const [validationError, setValidationError] = useState<string | null>(null);

  const catalog = useMemo<ParticipantRef[]>(() => {
    if (!treeQuery.data) return [];
    return flattenTree(treeQuery.data);
  }, [treeQuery.data]);

  // Catalog-aware label resolver: a chip's display name comes from the
  // catalog when the entry has loaded, otherwise it falls back to the
  // ref's seeded label (typically the path itself for query-string
  // seeding before the tree loads). Computed at render — no effect
  // mutation of `selected` required.
  const selectedDisplay = useMemo<ParticipantRef[]>(() => {
    if (catalog.length === 0) return selected;
    return selected.map((ref) => {
      const hit = catalog.find(
        (c) => c.scheme === ref.scheme && c.path === ref.path,
      );
      return hit && hit.label !== ref.label
        ? { ...ref, label: hit.label }
        : ref;
    });
  }, [catalog, selected]);

  const send = useMutation({
    mutationFn: async () => {
      if (selected.length === 0) {
        throw new Error("Pick at least one participant.");
      }
      if (!body.trim()) {
        throw new Error("Write a first message.");
      }
      const first = selected[0]!;
      const seed = await api.sendMessage({
        to: { scheme: first.scheme, path: first.path },
        type: "Domain",
        threadId: null,
        payload: body.trim(),
      });
      const threadId = seed.threadId;
      if (!threadId) {
        throw new Error(
          "The server did not return a thread id; the engagement was not created.",
        );
      }
      const fanoutErrors: string[] = [];
      for (const ref of selected.slice(1)) {
        try {
          await api.sendMessage({
            to: { scheme: ref.scheme, path: ref.path },
            type: "Domain",
            threadId,
            payload: body.trim(),
          });
        } catch (err) {
          const message = err instanceof Error ? err.message : String(err);
          fanoutErrors.push(refKey(ref) + ": " + message);
        }
      }
      return { threadId, fanoutErrors };
    },
    onSuccess: ({ threadId, fanoutErrors }) => {
      if (fanoutErrors.length > 0) {
        toast({
          title:
            "Engagement started — some participants did not receive the seed",
          description: fanoutErrors.join("\n"),
          variant: "destructive",
        });
      } else {
        toast({ title: "Engagement started" });
      }
      router.push("/engagement/" + threadId);
    },
    onError: (err) => {
      const message = err instanceof Error ? err.message : String(err);
      setValidationError(message);
    },
  });

  const submit = useCallback(
    (e: React.FormEvent) => {
      e.preventDefault();
      setValidationError(null);
      if (selected.length === 0) {
        setValidationError("Pick at least one participant.");
        return;
      }
      if (!body.trim()) {
        setValidationError("Write a first message.");
        return;
      }
      send.mutate();
    },
    [selected, body, send],
  );

  const togglePick = useCallback((ref: ParticipantRef) => {
    setSelected((prev) => {
      const key = refKey(ref);
      const exists = prev.some((p) => refKey(p) === key);
      if (exists) return prev.filter((p) => refKey(p) !== key);
      return [...prev, ref];
    });
  }, []);

  const remove = useCallback((ref: ParticipantRef) => {
    setSelected((prev) => prev.filter((p) => refKey(p) !== refKey(ref)));
  }, []);

  const filteredCatalog = useMemo(() => {
    const needle = filter.trim().toLowerCase();
    if (!needle) return catalog;
    return catalog.filter(
      (c) =>
        c.path.toLowerCase().includes(needle) ||
        c.label.toLowerCase().includes(needle),
    );
  }, [catalog, filter]);

  const selectedKeys = useMemo(
    () => new Set(selected.map(refKey)),
    [selected],
  );

  return (
    <form
      onSubmit={submit}
      className="space-y-4"
      data-testid="engagement-new-form"
      aria-label="Start a new engagement"
    >
      <Card>
        <CardHeader>
          <CardTitle className="flex items-center gap-2 text-base">
            <MessagesSquare className="h-4 w-4" /> Participants
          </CardTitle>
        </CardHeader>
        <CardContent className="space-y-3">
          <div
            className="flex flex-wrap gap-2"
            data-testid="engagement-new-selected"
          >
            {selectedDisplay.length === 0 && (
              <p className="text-xs text-muted-foreground">
                No participants picked yet.
              </p>
            )}
            {selectedDisplay.map((ref) => (
              <span
                key={refKey(ref)}
                data-testid={
                  "engagement-new-chip-" + ref.scheme + "-" + ref.path
                }
                className="inline-flex items-center gap-1 rounded-md border border-border bg-muted/40 px-2 py-1 text-xs"
              >
                {ref.scheme === "unit" ? (
                  <Layers className="h-3.5 w-3.5" aria-hidden="true" />
                ) : (
                  <Bot className="h-3.5 w-3.5" aria-hidden="true" />
                )}
                <span className="font-medium">{ref.label}</span>
                <span className="font-mono text-[10px] text-muted-foreground">
                  {ref.scheme}://{ref.path}
                </span>
                <button
                  type="button"
                  onClick={() => remove(ref)}
                  data-testid={
                    "engagement-new-chip-remove-" +
                    ref.scheme +
                    "-" +
                    ref.path
                  }
                  aria-label={"Remove " + ref.scheme + "://" + ref.path}
                  className="ml-1 rounded-sm text-muted-foreground hover:text-foreground focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring"
                >
                  <X className="h-3 w-3" aria-hidden="true" />
                </button>
              </span>
            ))}
          </div>

          <div className="border-t border-border pt-3">
            <label className="block space-y-1">
              <span className="text-xs text-muted-foreground">
                Search units &amp; agents
              </span>
              <Input
                value={filter}
                onChange={(e) => setFilter(e.target.value)}
                placeholder="Filter by name or address…"
                data-testid="engagement-new-filter"
                autoComplete="off"
              />
            </label>
            <div
              className="mt-2 max-h-48 overflow-auto rounded-md border border-border"
              data-testid="engagement-new-catalog"
            >
              {treeQuery.isPending && (
                <div className="space-y-1 p-2">
                  <Skeleton className="h-6 w-full" />
                  <Skeleton className="h-6 w-full" />
                  <Skeleton className="h-6 w-full" />
                </div>
              )}
              {treeQuery.isError && (
                <p
                  className="p-2 text-xs text-destructive"
                  data-testid="engagement-new-catalog-error"
                >
                  Failed to load tenant tree:{" "}
                  {treeQuery.error instanceof Error
                    ? treeQuery.error.message
                    : "unknown error"}
                </p>
              )}
              {!treeQuery.isPending &&
                !treeQuery.isError &&
                filteredCatalog.length === 0 && (
                  <p
                    className="p-2 text-xs text-muted-foreground"
                    data-testid="engagement-new-catalog-empty"
                  >
                    No units or agents match.
                  </p>
                )}
              <ul className="divide-y divide-border">
                {filteredCatalog.map((ref) => {
                  const picked = selectedKeys.has(refKey(ref));
                  return (
                    <li key={refKey(ref)}>
                      <button
                        type="button"
                        onClick={() => togglePick(ref)}
                        data-testid={
                          "engagement-new-pick-" +
                          ref.scheme +
                          "-" +
                          ref.path
                        }
                        aria-pressed={picked}
                        className={cn(
                          "flex w-full items-center gap-2 px-3 py-2 text-left text-sm transition-colors",
                          picked
                            ? "bg-primary/5 text-primary"
                            : "hover:bg-muted/50",
                        )}
                      >
                        {ref.scheme === "unit" ? (
                          <Layers
                            className="h-4 w-4 shrink-0"
                            aria-hidden="true"
                          />
                        ) : (
                          <Bot
                            className="h-4 w-4 shrink-0"
                            aria-hidden="true"
                          />
                        )}
                        <span className="min-w-0 flex-1 truncate font-medium">
                          {ref.label}
                        </span>
                        <span className="hidden font-mono text-[10px] text-muted-foreground sm:inline">
                          {ref.scheme}://{ref.path}
                        </span>
                        {picked && (
                          <Badge variant="outline" className="ml-2 shrink-0">
                            picked
                          </Badge>
                        )}
                        {!picked && (
                          <Plus
                            className="h-4 w-4 shrink-0 text-muted-foreground"
                            aria-hidden="true"
                          />
                        )}
                      </button>
                    </li>
                  );
                })}
              </ul>
            </div>
          </div>
        </CardContent>
      </Card>

      <Card>
        <CardHeader>
          <CardTitle className="text-base">Opening message</CardTitle>
        </CardHeader>
        <CardContent>
          <label htmlFor="engagement-new-body-input" className="sr-only">
            First message
          </label>
          <textarea
            id="engagement-new-body-input"
            value={body}
            onChange={(e) => setBody(e.target.value)}
            placeholder="What do you want them to do?"
            rows={5}
            data-testid="engagement-new-body"
            className="flex min-h-[96px] w-full resize-y rounded-md border border-input bg-background px-3 py-2 text-sm shadow-sm focus-visible:outline-none focus-visible:ring-1 focus-visible:ring-ring"
            aria-label="Opening message"
          />
        </CardContent>
      </Card>

      {validationError && (
        <p
          role="alert"
          data-testid="engagement-new-error"
          className="rounded-md border border-destructive/50 bg-destructive/10 px-3 py-2 text-xs text-destructive"
        >
          {validationError}
        </p>
      )}

      <div className="flex items-center justify-end gap-2">
        <Button
          type="button"
          variant="ghost"
          onClick={() => router.push("/engagement/mine")}
          data-testid="engagement-new-cancel"
          disabled={send.isPending}
        >
          Cancel
        </Button>
        <Button
          type="submit"
          data-testid="engagement-new-submit"
          disabled={send.isPending}
        >
          {send.isPending ? (
            <>
              <Loader2
                className="mr-1 h-3.5 w-3.5 animate-spin"
                aria-hidden="true"
              />
              Starting…
            </>
          ) : (
            "Start engagement"
          )}
        </Button>
      </div>
    </form>
  );
}
