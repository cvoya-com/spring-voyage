"use client";

// Unified Deployment tab (canonical-tabs.md § 5.12, #2273, #2274).
//
// Dedicated surface for the persistent runtime lifecycle:
//   Agent → LifecyclePanel (deploy/undeploy/scale/logs)
//   Unit  → UnitLifecyclePanel (start/stop/status, #2274)
//
// The canonical control accepts `{ kind, id }`.

import { useState } from "react";
import { useQueryClient } from "@tanstack/react-query";
import { Activity, Play, Square } from "lucide-react";

import { Badge } from "@/components/ui/badge";
import { Button } from "@/components/ui/button";
import { ConfirmDialog } from "@/components/ui/confirm-dialog";
import { useToast } from "@/components/ui/toast";
import { LifecyclePanel } from "@/components/agents/tab-impls/lifecycle-panel";
import { api } from "@/lib/api/client";
import { useUnit, useUnitDeployment } from "@/lib/api/queries";
import { queryKeys } from "@/lib/api/query-keys";
import { formatTranslatedError } from "@/lib/api/translate-error";

export type DeploymentSubjectKind = "Unit" | "Agent";

export interface DeploymentTabProps {
  /** Subject kind — drives the body selection (panel vs placeholder). */
  kind: DeploymentSubjectKind;
  /** Stable id of the subject (unit id or agent id). */
  id: string;
}

export function DeploymentTab({ kind, id }: DeploymentTabProps) {
  if (kind === "Unit") {
    return <UnitLifecyclePanel unitId={id} />;
  }
  return (
    <div className="space-y-6" data-testid="tab-agent-deployment">
      <header className="flex items-center gap-2 text-sm text-muted-foreground">
        <Activity className="h-4 w-4" aria-hidden="true" />
        <span>
          Persistent container lifecycle for this agent. Mirrors{" "}
          <code className="rounded bg-muted px-1 py-0.5 font-mono text-xs">
            spring agent deploy / undeploy / scale / logs
          </code>
          . Destructive actions (undeploy, scale to 0) require confirmation.
        </span>
      </header>
      <LifecyclePanel agentId={id} />
    </div>
  );
}

function UnitLifecyclePanel({ unitId }: { unitId: string }) {
  const { toast } = useToast();
  const queryClient = useQueryClient();
  const deploymentQuery = useUnitDeployment(unitId);
  const unitQuery = useUnit(unitId);
  const [pending, setPending] = useState<"start" | "stop" | null>(null);
  const [stopConfirmOpen, setStopConfirmOpen] = useState(false);

  const deployment = deploymentQuery.data;
  const running = deployment?.running ?? false;
  const status = deployment?.status ?? unitQuery.data?.status ?? "—";

  const invalidate = () => {
    queryClient.invalidateQueries({ queryKey: queryKeys.units.deployment(unitId) });
    queryClient.invalidateQueries({ queryKey: queryKeys.units.detail(unitId) });
  };

  const handleStart = async () => {
    setPending("start");
    try {
      await api.startUnit(unitId);
      invalidate();
      toast({ title: "Unit started" });
    } catch (err) {
      toast({
        title: "Start failed",
        description: formatTranslatedError(err),
        variant: "destructive",
      });
    } finally {
      setPending(null);
    }
  };

  const handleStop = async () => {
    setStopConfirmOpen(false);
    setPending("stop");
    try {
      await api.stopUnit(unitId);
      invalidate();
      toast({ title: "Unit stopped" });
    } catch (err) {
      toast({
        title: "Stop failed",
        description: formatTranslatedError(err),
        variant: "destructive",
      });
    } finally {
      setPending(null);
    }
  };

  return (
    <div className="space-y-6" data-testid="tab-unit-deployment">
      <header className="flex items-center gap-2 text-sm text-muted-foreground">
        <Activity className="h-4 w-4" aria-hidden="true" />
        <span>
          Unit lifecycle — start and stop the unit&apos;s runtime. Mirrors{" "}
          <code className="rounded bg-muted px-1 py-0.5 font-mono text-xs">
            spring unit start / stop
          </code>
          . Stopping requires confirmation.
        </span>
      </header>

      <div className="flex items-center gap-3">
        <span className="text-sm text-muted-foreground">Status:</span>
        <Badge variant={running ? "success" : "default"}>{status}</Badge>
      </div>

      <div className="flex gap-2">
        <Button
          size="sm"
          disabled={running || pending !== null}
          onClick={handleStart}
          data-testid="tab-unit-deployment-start"
        >
          <Play className="mr-1 h-3 w-3" aria-hidden="true" />
          {pending === "start" ? "Starting…" : "Start"}
        </Button>
        <Button
          size="sm"
          variant="outline"
          disabled={!running || pending !== null}
          onClick={() => setStopConfirmOpen(true)}
          data-testid="tab-unit-deployment-stop"
        >
          <Square className="mr-1 h-3 w-3" aria-hidden="true" />
          Stop
        </Button>
      </div>

      <ConfirmDialog
        open={stopConfirmOpen}
        title="Stop unit?"
        description="Stopping the unit will terminate its runtime container. Any in-flight messages will be dropped."
        confirmLabel="Stop"
        confirmVariant="destructive"
        onConfirm={handleStop}
        onCancel={() => setStopConfirmOpen(false)}
      />
    </div>
  );
}
