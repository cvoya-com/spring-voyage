"use client";

// Agent Clones tab (EXP-tab-agent-clones, umbrella #815 §4).
//
// Read-only list of the agent's clones, mirroring the data the legacy
// agent detail page surfaces. Clone creation/deletion ride the same
// `/agents/[id]` mutations that don't belong in the Explorer's quick
// view — reviewers who need to spawn a clone still land on the owner
// route or use `spring agent clone` on the CLI.

import { Copy } from "lucide-react";

import { Badge } from "@/components/ui/badge";
import { useAgentClones } from "@/lib/api/queries";
import { timeAgo } from "@/lib/utils";

import { registerTab, type TabContentProps } from "./index";

function AgentClonesTab({ node }: TabContentProps) {
  const { data, isLoading, error } = useAgentClones(node.id);
  if (node.kind !== "Agent") return null;

  if (isLoading) {
    return (
      <p
        role="status"
        aria-live="polite"
        className="text-sm text-muted-foreground"
        data-testid="tab-agent-clones-loading"
      >
        Loading clones…
      </p>
    );
  }

  if (error) {
    return (
      <p
        role="alert"
        className="rounded-md border border-destructive/50 bg-destructive/10 px-3 py-2 text-sm text-destructive"
        data-testid="tab-agent-clones-error"
      >
        Couldn&apos;t load clones:{" "}
        {error instanceof Error ? error.message : String(error)}
      </p>
    );
  }

  const clones = data ?? [];

  if (clones.length === 0) {
    return (
      <div
        data-testid="tab-agent-clones-empty"
        className="rounded-lg border border-dashed border-border bg-muted/30 p-6 text-center"
      >
        <Copy className="mx-auto h-6 w-6 text-muted-foreground" aria-hidden="true" />
        <p className="mt-2 text-sm font-medium">No clones yet</p>
        <p className="mt-1 text-xs text-muted-foreground">
          Create one from the agent detail page or{" "}
          <code className="rounded bg-muted px-1 py-0.5 text-xs">
            spring agent clone
          </code>
          .
        </p>
      </div>
    );
  }

  return (
    <ul
      className="space-y-2"
      data-testid="tab-agent-clones"
      aria-label={`Clones of agent ${node.name}`}
    >
      {clones.map((c) => (
        <li
          key={c.cloneId}
          className="flex flex-col gap-2 rounded-md border border-border p-3 text-sm sm:flex-row sm:items-center sm:justify-between"
        >
          <div className="min-w-0 space-y-1">
            <div className="truncate font-mono text-xs">{c.cloneId}</div>
            <div className="flex flex-wrap items-center gap-2 text-xs text-muted-foreground">
              <Badge variant="outline">{c.cloneType}</Badge>
              <Badge variant="outline">{c.attachmentMode}</Badge>
              <Badge
                variant={c.status === "active" ? "success" : "default"}
              >
                {c.status}
              </Badge>
              <span>{timeAgo(c.createdAt)}</span>
            </div>
          </div>
        </li>
      ))}
    </ul>
  );
}

registerTab("Agent", "Clones", AgentClonesTab);

export default AgentClonesTab;
