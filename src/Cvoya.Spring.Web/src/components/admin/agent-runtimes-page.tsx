"use client";

/**
 * Agent-runtimes admin surface (#691).
 *
 * Rendered at `/settings/agent-runtimes`. Surfaces the tenant's
 * installed agent runtimes, their model lists, and the persistent
 * credential-health row written by accept-time validation and the
 * watchdog middleware. Every mutation (install, uninstall, configure,
 * credential validation) ships through the `spring agent-runtime …`
 * CLI per the AGENTS.md carve-out.
 *
 * Cross-reference: `docs/guide/operator/agent-runtimes.md` covers the CLI
 * workflows the "Operator guide" link deep-links into.
 */

import { Cpu } from "lucide-react";

import { Card, CardContent } from "@/components/ui/card";
import { Skeleton } from "@/components/ui/skeleton";
import {
  useAgentRuntimeCredentialHealth,
  useAgentRuntimes,
} from "@/lib/api/queries";
import type { InstalledAgentRuntimeResponse } from "@/lib/api/types";

import { CliCallout, CredentialHealthBadge, Timestamp } from "./shared";

export default function AgentRuntimesAdminPage() {
  const query = useAgentRuntimes();
  const runtimes = query.data ?? [];
  const loading = query.isPending;

  return (
    <div className="space-y-6">
      <div>
        <h1 className="flex items-center gap-2 text-2xl font-bold">
          <Cpu className="h-5 w-5" aria-hidden="true" /> Agent runtimes
        </h1>
        <p className="text-sm text-muted-foreground">
          Installed agent runtimes on the current tenant, their model
          catalogs, and credential-health status.
        </p>
      </div>

      <CliCallout
        cliCommand="spring agent-runtime"
        docsHref="/docs/guide/operator/agent-runtimes.md"
        docsLabel="Operator guide"
      />

      {loading ? (
        <div className="space-y-3">
          <Skeleton className="h-24" />
          <Skeleton className="h-24" />
          <Skeleton className="h-24" />
        </div>
      ) : query.error ? (
        <Card>
          <CardContent className="p-6">
            <p className="text-sm text-destructive" role="alert">
              Failed to load agent runtimes: {query.error.message}
            </p>
          </CardContent>
        </Card>
      ) : runtimes.length === 0 ? (
        <Card>
          <CardContent className="space-y-2 p-6 text-center">
            <Cpu
              className="mx-auto h-10 w-10 text-muted-foreground"
              aria-hidden="true"
            />
            <p className="text-sm text-muted-foreground">
              No agent runtimes installed on this tenant. Install one with{" "}
              <code className="rounded bg-muted px-1 py-0.5 text-xs">
                spring agent-runtime install &lt;id&gt;
              </code>
              .
            </p>
          </CardContent>
        </Card>
      ) : (
        <div
          className="space-y-3"
          data-testid="admin-agent-runtimes-list"
        >
          {runtimes.map((runtime) => (
            <RuntimeRow key={runtime.id} runtime={runtime} />
          ))}
        </div>
      )}
    </div>
  );
}

function RuntimeRow({ runtime }: { runtime: InstalledAgentRuntimeResponse }) {
  const healthQuery = useAgentRuntimeCredentialHealth(runtime.id);
  const healthStatus = healthQuery.data?.status ?? null;
  const lastChecked = healthQuery.data?.lastChecked ?? null;
  const lastError = healthQuery.data?.lastError ?? null;

  return (
    <Card
      data-testid={`admin-agent-runtime-row-${runtime.id}`}
      className="transition-colors"
    >
      <CardContent className="space-y-3 p-4">
        <div className="flex flex-wrap items-start justify-between gap-2">
          <div className="min-w-0">
            <div className="flex flex-wrap items-center gap-2">
              <h2 className="truncate text-base font-semibold">
                {runtime.displayName}
              </h2>
              <code
                className="rounded bg-muted px-1 py-0.5 font-mono text-xs text-muted-foreground"
                aria-label="Runtime id"
              >
                {runtime.id}
              </code>
              <span
                className="text-xs text-muted-foreground"
                aria-label="Tool kind"
              >
                {runtime.toolKind}
              </span>
            </div>
            <p className="mt-1 text-xs text-muted-foreground">
              Installed <Timestamp value={runtime.installedAt} />
              {runtime.credentialKind !== "None" && (
                <>
                  {" "}
                  · credential:{" "}
                  <code className="rounded bg-muted px-1 py-0.5 font-mono text-[11px]">
                    {runtime.credentialKind}
                  </code>
                </>
              )}
            </p>
          </div>
          <div
            className="flex flex-col items-end gap-1 text-xs"
            aria-label={`Credential health for ${runtime.displayName}`}
          >
            <CredentialHealthBadge
              status={healthStatus}
              data-testid={`admin-agent-runtime-health-${runtime.id}`}
            />
            {lastChecked && (
              <span className="text-muted-foreground">
                Checked <Timestamp value={lastChecked} />
              </span>
            )}
          </div>
        </div>

        <div>
          <div className="mb-1 text-xs font-medium uppercase tracking-wide text-muted-foreground">
            Models ({runtime.models.length})
          </div>
          <ModelList
            models={runtime.models}
            defaultModel={runtime.defaultModel ?? null}
          />
        </div>

        {lastError && healthStatus !== "Valid" && (
          <p className="rounded-md border border-destructive/30 bg-destructive/5 px-3 py-2 text-xs text-destructive">
            <span className="font-semibold">Last error:</span> {lastError}
          </p>
        )}
      </CardContent>
    </Card>
  );
}

function ModelList({
  models,
  defaultModel,
}: {
  models: readonly string[];
  defaultModel: string | null;
}) {
  if (models.length === 0) {
    return (
      <p className="text-xs text-muted-foreground">
        (no models configured — the runtime reports its catalog at install time)
      </p>
    );
  }
  const visible = models.slice(0, 6);
  const remainder = models.length - visible.length;
  return (
    <ul
      className="flex flex-wrap gap-1.5"
      aria-label="Configured models"
    >
      {visible.map((model) => (
        <li key={model}>
          <code
            className={`rounded px-1.5 py-0.5 font-mono text-xs ${
              model === defaultModel
                ? "bg-primary/15 text-primary"
                : "bg-muted text-muted-foreground"
            }`}
          >
            {model}
            {model === defaultModel && " · default"}
          </code>
        </li>
      ))}
      {remainder > 0 && (
        <li
          className="text-xs text-muted-foreground"
          aria-label={`${remainder} more models not shown`}
        >
          +{remainder} more
        </li>
      )}
    </ul>
  );
}
