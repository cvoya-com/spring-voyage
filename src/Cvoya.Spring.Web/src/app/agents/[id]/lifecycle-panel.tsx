"use client";

/**
 * Portal surface for the persistent-agent lifecycle verbs (#396 /
 * PR-PLAT-RUN-2b). Mirrors `spring agent {deploy,undeploy,scale,logs}`
 * 1:1 — every verb the CLI exposes is reachable from this panel.
 *
 * The panel is always visible on the agent detail page. Ephemeral
 * agents will receive a 400 from the lifecycle endpoints; we surface
 * the server's error verbatim (matching the CLI) rather than hiding
 * the controls, since the OSS core has no reliable signal on
 * `AgentResponse` that the agent is persistent before a first deploy.
 * The "Not deployed yet" empty state is the normal resting state for
 * both ephemeral agents and persistent agents that haven't been
 * brought up.
 *
 * Logs are a manual-refresh snapshot today, consistent with the CLI's
 * `spring agent logs`. A streaming upgrade is tracked as a follow-up.
 */

import { useState } from "react";
import { useQueryClient } from "@tanstack/react-query";
import {
  Activity,
  Loader2,
  Play,
  RefreshCw,
  Square,
  TerminalSquare,
} from "lucide-react";

import { Badge } from "@/components/ui/badge";
import { Button } from "@/components/ui/button";
import {
  Card,
  CardContent,
  CardHeader,
  CardTitle,
} from "@/components/ui/card";
import { useToast } from "@/components/ui/toast";
import { api } from "@/lib/api/client";
import { useAgentDeployment, useAgentLogs } from "@/lib/api/queries";
import { queryKeys } from "@/lib/api/query-keys";
import type { PersistentAgentDeploymentResponse } from "@/lib/api/types";
import { timeAgo } from "@/lib/utils";

type HealthVariant = "default" | "success" | "warning" | "destructive";

function healthBadgeVariant(status: string | null | undefined): HealthVariant {
  switch ((status ?? "").toLowerCase()) {
    case "healthy":
      return "success";
    case "unhealthy":
      return "destructive";
    case "unknown":
    default:
      return "default";
  }
}

interface LifecyclePanelProps {
  /** The agent identifier (same as `Agent.name` / the CLI's `<id>`). */
  agentId: string;
  /**
   * The deployment slot from `AgentDetailResponse.deployment`. Used only
   * as a seed — the panel owns its own `useAgentDeployment` query so
   * mutations here don't have to round-trip the outer detail query.
   */
  initialDeployment?: PersistentAgentDeploymentResponse | null;
}

export function LifecyclePanel({
  agentId,
  initialDeployment,
}: LifecyclePanelProps) {
  const { toast } = useToast();
  const queryClient = useQueryClient();

  const deploymentQuery = useAgentDeployment(agentId);
  // Prefer the live query's value; fall back to the seed from the parent
  // detail response so the panel renders something on first paint.
  const deployment = deploymentQuery.data ?? initialDeployment ?? null;

  const [imageOverride, setImageOverride] = useState("");
  const [tailInput, setTailInput] = useState("200");
  const [logsVisible, setLogsVisible] = useState(false);
  const [pendingVerb, setPendingVerb] = useState<
    "deploy" | "undeploy" | "scale" | null
  >(null);

  const tailNumber = Number.parseInt(tailInput, 10);
  const effectiveTail =
    Number.isFinite(tailNumber) && tailNumber > 0 ? tailNumber : 200;

  const logsQuery = useAgentLogs(agentId, effectiveTail, {
    // Only hit the logs endpoint when the user has opened the logs
    // panel. Avoids a 404 spam on agents that have never been deployed.
    enabled: logsVisible && Boolean(agentId),
  });

  const seedDeploymentCache = (next: PersistentAgentDeploymentResponse) => {
    queryClient.setQueryData(queryKeys.agents.deployment(agentId), next);
    // Also nudge the parent detail query so the header strip (if it
    // reads `data.deployment`) stays in sync on the next render.
    queryClient.invalidateQueries({
      queryKey: queryKeys.agents.detail(agentId),
    });
  };

  const handleDeploy = async () => {
    setPendingVerb("deploy");
    try {
      const image = imageOverride.trim();
      const result = await api.deployPersistentAgent(
        agentId,
        image ? { image } : undefined,
      );
      seedDeploymentCache(result);
      toast({
        title: "Deploy requested",
        description: result.running
          ? `Running (container ${(result.containerId ?? "").slice(0, 12)})`
          : "Server acknowledged — container is coming up.",
      });
    } catch (err) {
      toast({
        title: "Deploy failed",
        description: err instanceof Error ? err.message : String(err),
        variant: "destructive",
      });
    } finally {
      setPendingVerb(null);
    }
  };

  const handleUndeploy = async () => {
    setPendingVerb("undeploy");
    try {
      const result = await api.undeployPersistentAgent(agentId);
      seedDeploymentCache(result);
      toast({ title: "Undeploy requested" });
    } catch (err) {
      toast({
        title: "Undeploy failed",
        description: err instanceof Error ? err.message : String(err),
        variant: "destructive",
      });
    } finally {
      setPendingVerb(null);
    }
  };

  const handleScale = async (replicas: 0 | 1) => {
    setPendingVerb("scale");
    try {
      const result = await api.scalePersistentAgent(agentId, { replicas });
      seedDeploymentCache(result);
      toast({
        title: `Scaled to ${replicas}`,
        description:
          replicas === 0
            ? "Container has been undeployed."
            : "Container is being brought up.",
      });
    } catch (err) {
      toast({
        title: "Scale failed",
        description: err instanceof Error ? err.message : String(err),
        variant: "destructive",
      });
    } finally {
      setPendingVerb(null);
    }
  };

  const toggleLogs = () => {
    setLogsVisible((v) => !v);
  };

  const running = deployment?.running ?? false;
  const health = deployment?.healthStatus ?? "unknown";
  const endpoint = deployment?.endpoint ?? null;
  const containerId = deployment?.containerId ?? null;
  const image = deployment?.image ?? null;
  const startedAt = deployment?.startedAt ?? null;
  const consecutiveFailures = deployment?.consecutiveFailures ?? 0;
  const busy = pendingVerb !== null;

  return (
    // The `id` anchor is the deep-link target used by the Agents lens
    // (#450) — the "Deployment" quick action on each card resolves to
    // `/agents/{id}#deployment` and scrolls straight to this panel.
    <Card id="deployment" data-testid="agent-lifecycle-panel">
      <CardHeader>
        <CardTitle className="flex items-center gap-2">
          <Activity className="h-4 w-4" /> Persistent deployment
          <Badge
            variant={running ? "success" : "outline"}
            className="ml-2"
            data-testid="agent-lifecycle-running-badge"
          >
            {running ? "Running" : "Not deployed"}
          </Badge>
          {running && (
            <Badge variant={healthBadgeVariant(health)}>{health}</Badge>
          )}
        </CardTitle>
      </CardHeader>
      <CardContent className="space-y-4 text-sm">
        <p className="text-xs text-muted-foreground">
          Mirrors{" "}
          <code className="rounded bg-muted px-1 py-0.5 font-mono text-xs">
            spring agent deploy / undeploy / scale / logs
          </code>
          . Ephemeral agents are rejected by the server with a 400 — the
          error surfaces here as a toast.
        </p>

        {running && (
          <dl
            className="grid grid-cols-1 gap-2 rounded-md border border-border p-3 text-xs sm:grid-cols-2"
            data-testid="agent-lifecycle-details"
          >
            <div>
              <dt className="text-muted-foreground">Image</dt>
              <dd className="truncate font-mono">{image ?? "—"}</dd>
            </div>
            <div>
              <dt className="text-muted-foreground">Endpoint</dt>
              <dd className="truncate font-mono">{endpoint ?? "—"}</dd>
            </div>
            <div>
              <dt className="text-muted-foreground">Container</dt>
              <dd className="truncate font-mono">
                {containerId ? containerId.slice(0, 12) : "—"}
              </dd>
            </div>
            <div>
              <dt className="text-muted-foreground">Started</dt>
              <dd>{startedAt ? timeAgo(startedAt) : "—"}</dd>
            </div>
            <div>
              <dt className="text-muted-foreground">
                Consecutive health failures
              </dt>
              <dd>{consecutiveFailures}</dd>
            </div>
            <div>
              <dt className="text-muted-foreground">Replicas</dt>
              <dd>{deployment?.replicas ?? 0}</dd>
            </div>
          </dl>
        )}

        <div className="grid grid-cols-1 gap-3 sm:grid-cols-[1fr_auto]">
          <label className="block space-y-1">
            <span className="text-xs text-muted-foreground">
              Image override (optional)
            </span>
            <input
              type="text"
              value={imageOverride}
              onChange={(e) => setImageOverride(e.target.value)}
              placeholder="e.g. ghcr.io/cvoya-com/spring-agent:2.1.98"
              className="flex h-9 w-full rounded-md border border-input bg-background px-3 text-sm shadow-sm focus-visible:outline-none focus-visible:ring-1 focus-visible:ring-ring"
              data-testid="agent-lifecycle-image-input"
            />
          </label>
          <div className="flex items-end">
            <Button
              onClick={handleDeploy}
              disabled={busy}
              data-testid="agent-lifecycle-deploy"
            >
              {pendingVerb === "deploy" ? (
                <Loader2 className="mr-1 h-4 w-4 animate-spin" />
              ) : (
                <Play className="mr-1 h-4 w-4" />
              )}
              {running ? "Redeploy" : "Deploy"}
            </Button>
          </div>
        </div>

        <div className="flex flex-wrap items-center gap-2">
          <Button
            variant="outline"
            size="sm"
            onClick={handleUndeploy}
            disabled={busy}
            data-testid="agent-lifecycle-undeploy"
          >
            <Square className="mr-1 h-4 w-4" /> Undeploy
          </Button>
          <Button
            variant="outline"
            size="sm"
            onClick={() => handleScale(1)}
            disabled={busy}
            data-testid="agent-lifecycle-scale-up"
          >
            Scale to 1
          </Button>
          <Button
            variant="outline"
            size="sm"
            onClick={() => handleScale(0)}
            disabled={busy}
            data-testid="agent-lifecycle-scale-zero"
          >
            Scale to 0
          </Button>
          <Button
            variant="ghost"
            size="sm"
            onClick={() => deploymentQuery.refetch()}
            disabled={busy || deploymentQuery.isFetching}
            aria-label="Refresh deployment status"
            className="ml-auto"
          >
            <RefreshCw
              className={`h-4 w-4 ${deploymentQuery.isFetching ? "animate-spin" : ""}`}
            />
          </Button>
        </div>

        <div className="space-y-2 border-t border-border pt-4">
          <div className="flex flex-wrap items-center gap-2">
            <Button
              variant="outline"
              size="sm"
              onClick={toggleLogs}
              data-testid="agent-lifecycle-logs-toggle"
            >
              <TerminalSquare className="mr-1 h-4 w-4" />
              {logsVisible ? "Hide logs" : "Show logs"}
            </Button>
            {logsVisible && (
              <>
                <label className="flex items-center gap-1 text-xs text-muted-foreground">
                  Tail
                  <input
                    type="number"
                    inputMode="numeric"
                    min="1"
                    value={tailInput}
                    onChange={(e) => setTailInput(e.target.value)}
                    className="ml-1 h-8 w-20 rounded-md border border-input bg-background px-2 text-sm shadow-sm focus-visible:outline-none focus-visible:ring-1 focus-visible:ring-ring"
                    data-testid="agent-lifecycle-tail-input"
                  />
                </label>
                <Button
                  variant="ghost"
                  size="sm"
                  onClick={() => logsQuery.refetch()}
                  disabled={logsQuery.isFetching}
                  data-testid="agent-lifecycle-logs-refresh"
                >
                  <RefreshCw
                    className={`h-3.5 w-3.5 ${logsQuery.isFetching ? "animate-spin" : ""}`}
                  />
                </Button>
              </>
            )}
          </div>
          {logsVisible && (
            <pre
              className="max-h-96 overflow-auto rounded-md border border-border bg-muted p-3 font-mono text-xs leading-relaxed"
              aria-live="polite"
              data-testid="agent-lifecycle-logs-pane"
            >
              {logsQuery.isLoading && !logsQuery.data
                ? "Loading logs…"
                : logsQuery.data?.logs?.length
                  ? logsQuery.data.logs
                  : "No log output yet. Deploy the agent or wait for the container to emit output."}
            </pre>
          )}
        </div>
      </CardContent>
    </Card>
  );
}
