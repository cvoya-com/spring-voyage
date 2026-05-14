"use client";

// Unified Deployment tab (canonical-tabs.md § 5.12, #2273).
//
// Dedicated surface for the persistent-agent lifecycle verbs:
//   deploy / undeploy / scale / status / logs
//
// The canonical control accepts `{ kind, id }`. For `kind === "Agent"`
// it delegates to `<LifecyclePanel>` and renders the full
// deploy/undeploy/scale/logs surface mirroring `spring agent {deploy,
// undeploy,scale,logs}` 1:1.
//
// For `kind === "Unit"` the body renders a "Deploy via CLI for now"
// placeholder rather than 404-ing against the agent-keyed endpoints.
// The lifecycle endpoints (`useAgentDeployment`, `useAgentLogs`,
// `api.scalePersistentAgent`, etc.) are strictly agent-keyed today;
// see #2274 for the endpoint follow-up. Per canonical-tabs.md § 4 row
// 12 and `docs/concepts/units-vs-agents.md` rule 3 — a unit is an
// agent and the Deployment surface applies identically to both
// subjects; the deferral is an endpoint-side gap, not a domain-model
// one.

import { Activity } from "lucide-react";

import { LifecyclePanel } from "@/components/agents/tab-impls/lifecycle-panel";

export type DeploymentSubjectKind = "Unit" | "Agent";

export interface DeploymentTabProps {
  /** Subject kind — drives the body selection (panel vs placeholder). */
  kind: DeploymentSubjectKind;
  /** Stable id of the subject (unit id or agent id). */
  id: string;
}

export function DeploymentTab({ kind, id }: DeploymentTabProps) {
  if (kind === "Unit") {
    return <UnitDeploymentPlaceholder unitId={id} />;
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

function UnitDeploymentPlaceholder({ unitId }: { unitId: string }) {
  return (
    <div
      className="space-y-3 rounded-lg border border-dashed border-border bg-muted/30 p-6 text-sm"
      data-testid="tab-unit-deployment-cli-placeholder"
    >
      <div className="flex items-start gap-2">
        <Activity
          className="mt-0.5 h-5 w-5 shrink-0 text-muted-foreground"
          aria-hidden="true"
        />
        <div className="space-y-2">
          <p className="font-medium">Deploy via the CLI for now</p>
          <p className="text-xs text-muted-foreground">
            The portal will surface this unit&apos;s persistent-container
            lifecycle inline once unit-keyed deployment endpoints land. In
            the meantime the CLI is the canonical surface — every verb a
            unit can run is reachable through{" "}
            <code className="rounded bg-muted px-1 py-0.5 font-mono text-xs">
              spring agent deploy / undeploy / scale / logs
            </code>{" "}
            against the underlying agent.
          </p>
          <p className="text-xs text-muted-foreground">
            See{" "}
            <a
              href="https://github.com/cvoya-com/spring-voyage/issues/2274"
              className="underline"
              target="_blank"
              rel="noreferrer"
            >
              #2274
            </a>{" "}
            for the endpoint follow-up.
          </p>
          <p className="font-mono text-xs">
            spring agent deploy {unitId}
            <br />
            spring agent scale {unitId} --replicas 1
            <br />
            spring agent logs {unitId} --tail 200
          </p>
        </div>
      </div>
    </div>
  );
}
