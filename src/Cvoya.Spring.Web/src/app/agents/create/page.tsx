"use client";

import { useCallback, useState } from "react";
import { useRouter } from "next/navigation";
import { ArrowLeft } from "lucide-react";

import {
  AgentCreateForm,
  type AgentCreateFormSnapshot,
} from "@/components/agents/create-form";
import { Breadcrumbs } from "@/components/breadcrumbs";
import { Button } from "@/components/ui/button";
import {
  AGENT_WIZARD_STATE_SCHEMA_VERSION,
  clearAgentWizardSnapshot,
  loadAgentWizardSnapshot,
  saveAgentWizardSnapshot,
  type AgentWizardSnapshot,
} from "./wizard-persistence";

/**
 * New-agent wizard — scratch path (ADR-0039 K6).
 *
 * The page is a thin wrapper around `<AgentCreateForm>` (extracted in
 * ADR-0039 I3 so the unit-tab dialog (J1) can embed the same form). It
 * owns the page chrome (breadcrumbs, heading, copy) and translates the
 * form's success / cancel callbacks into router navigation. All form
 * state, validation, and the direct create flow live in the extracted
 * component.
 *
 * I7 audit (ADR-0039): the I3 extraction already delivered the I7
 * deliverable — this page is the thin wrapper the plan asked for. No
 * code changes required; this comment records the audit outcome.
 *
 * Behaviour preserved from the pre-extraction page:
 *  - `handleCancel` calls `router.back()`.
 *  - `handleSuccess` redirects to `/units?node=<first>&tab=Agents` when
 *    at least one unit was assigned, and to `/units` otherwise.
 *
 * Visual chrome reuses the existing Card / Input / Button primitives —
 * DESIGN.md does not need an update for this extraction.
 */
export default function CreateAgentPage() {
  const router = useRouter();

  const [initialSnapshot] = useState<AgentWizardSnapshot | null>(() => {
    if (typeof window === "undefined") return null;
    return loadAgentWizardSnapshot();
  });

  const handleSnapshotChange = useCallback(
    (snapshot: AgentCreateFormSnapshot) => {
      saveAgentWizardSnapshot({
        schemaVersion: AGENT_WIZARD_STATE_SCHEMA_VERSION,
        ...snapshot,
      });
    },
    [],
  );

  const handleSuccess = ({ unitIds }: { unitIds: string[] }) => {
    clearAgentWizardSnapshot();
    const target = unitIds[0]?.trim();
    if (target) {
      router.push(`/units?node=${encodeURIComponent(target)}&tab=Agents`);
    } else {
      router.push("/units");
    }
  };

  const handleCancel = () => {
    clearAgentWizardSnapshot();
    router.back();
  };

  return (
    <div className="mx-auto flex w-full max-w-3xl flex-col gap-6 px-4 py-8 sm:px-6 lg:px-8">
      <Breadcrumbs
        items={[
          { label: "Dashboard", href: "/" },
          { label: "Units", href: "/units" },
          { label: "New agent" },
        ]}
      />

      <div className="flex items-center justify-between gap-4">
        <div className="space-y-1">
          <h1 className="text-2xl font-semibold tracking-tight">
            Create a new agent
          </h1>
          <p className="text-sm text-muted-foreground">
            Posts directly to{" "}
            <code className="rounded bg-muted px-1 py-0.5 text-xs font-mono">
              /api/v1/tenant/agents
            </code>
            .
          </p>
        </div>
        <Button
          variant="outline"
          size="sm"
          onClick={handleCancel}
        >
          <ArrowLeft className="mr-1 h-4 w-4" />
          Back
        </Button>
      </div>

      <AgentCreateForm
        context="page"
        initialSnapshot={initialSnapshot ?? undefined}
        onSnapshotChange={handleSnapshotChange}
        onSuccess={handleSuccess}
        onCancel={handleCancel}
      />
    </div>
  );
}
