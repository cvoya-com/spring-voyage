"use client";

// Agent Config → General sub-tab (#2331). First tab in the Config strip;
// every directory- and actor-owned metadata field that previously could
// only be set at create time or via the CLI (displayName, description,
// role, model hint, specialty, enabled, executionMode) plus the existing
// expertise editor folded in.

import { useMemo, useState } from "react";
import { useMutation, useQueryClient } from "@tanstack/react-query";
import { Settings2 } from "lucide-react";

import { AgentExpertisePanel } from "@/components/expertise/agent-expertise-panel";
import { Button } from "@/components/ui/button";
import {
  Card,
  CardContent,
  CardHeader,
  CardTitle,
} from "@/components/ui/card";
import { Input } from "@/components/ui/input";
import { Skeleton } from "@/components/ui/skeleton";
import { useToast } from "@/components/ui/toast";
import { api } from "@/lib/api/client";
import { useAgent } from "@/lib/api/queries";
import { queryKeys } from "@/lib/api/query-keys";
import { formatTranslatedError } from "@/lib/api/translate-error";
import type { AgentExecutionMode, UpdateAgentMetadataRequest } from "@/lib/api/types";

interface AgentGeneralPanelProps {
  agentId: string;
}

interface DraftFields {
  displayName: string;
  description: string;
  role: string;
  model: string;
  specialty: string;
  enabled: boolean;
  executionMode: AgentExecutionMode;
}

const EMPTY_DRAFT: DraftFields = {
  displayName: "",
  description: "",
  role: "",
  model: "",
  specialty: "",
  enabled: true,
  executionMode: "Auto",
};

const EXECUTION_MODES: readonly AgentExecutionMode[] = ["Auto", "OnDemand"];

export function AgentGeneralPanel({ agentId }: AgentGeneralPanelProps) {
  const { toast } = useToast();
  const queryClient = useQueryClient();
  const agentQuery = useAgent(agentId);

  const persisted: DraftFields = useMemo(() => {
    const agent = agentQuery.data?.agent;
    return agent
      ? {
          displayName: agent.displayName ?? "",
          description: agent.description ?? "",
          role: agent.role ?? "",
          model: agent.model ?? "",
          specialty: agent.specialty ?? "",
          enabled: agent.enabled ?? true,
          executionMode: agent.executionMode ?? "Auto",
        }
      : EMPTY_DRAFT;
  }, [agentQuery.data]);

  const [draft, setDraft] = useState<DraftFields>(EMPTY_DRAFT);
  const [seededFor, setSeededFor] = useState<string | null>(null);
  const fingerprint = `${agentId}:${persisted.displayName}:${persisted.description}:${persisted.role}:${persisted.model}:${persisted.specialty}:${persisted.enabled}:${persisted.executionMode}`;

  // Render-phase derived-state pattern (same shape <InstructionsPanel> uses)
  // so the form seeds from the latest persisted snapshot without paying a
  // cascading effect render. React optimizes setState during render when it
  // matches an existing setter on this component.
  if (fingerprint !== seededFor) {
    setDraft(persisted);
    setSeededFor(fingerprint);
  }

  const dirty =
    draft.displayName !== persisted.displayName ||
    draft.description !== persisted.description ||
    draft.role !== persisted.role ||
    draft.model !== persisted.model ||
    draft.specialty !== persisted.specialty ||
    draft.enabled !== persisted.enabled ||
    draft.executionMode !== persisted.executionMode;

  const saveMutation = useMutation({
    mutationFn: async () => {
      const patch: UpdateAgentMetadataRequest = {};
      if (draft.displayName !== persisted.displayName) {
        patch.displayName = draft.displayName;
      }
      if (draft.description !== persisted.description) {
        patch.description = draft.description;
      }
      if (draft.role !== persisted.role) {
        patch.role = draft.role;
      }
      if (draft.model !== persisted.model) {
        patch.model = draft.model;
      }
      if (draft.specialty !== persisted.specialty) {
        patch.specialty = draft.specialty;
      }
      if (draft.enabled !== persisted.enabled) {
        patch.enabled = draft.enabled;
      }
      if (draft.executionMode !== persisted.executionMode) {
        patch.executionMode = draft.executionMode;
      }
      await api.updateAgentMetadata(agentId, patch);
    },
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: queryKeys.agents.detail(agentId) });
      queryClient.invalidateQueries({ queryKey: queryKeys.directory.all });
      toast({ title: "Agent details saved" });
    },
    onError: (err) => {
      toast({
        title: "Save failed",
        description: formatTranslatedError(err),
        variant: "destructive",
      });
    },
  });

  if (agentQuery.isPending) {
    return <Skeleton className="h-64" data-testid="agent-general-skeleton" />;
  }

  return (
    <div className="space-y-4" data-testid="agent-general-panel">
      <Card>
        <CardHeader className="flex flex-row items-center gap-2 space-y-0 pb-2">
          <CardTitle className="flex items-center gap-2 text-base">
            <Settings2 className="h-4 w-4" />
            <span>General</span>
          </CardTitle>
        </CardHeader>
        <CardContent className="space-y-4">
          <p className="text-xs text-muted-foreground">
            Core metadata for this agent. Each field maps 1:1 to a field on{" "}
            <code className="rounded bg-muted px-1 py-0.5 text-xs">
              spring agent update
            </code>
            .
          </p>

          <label className="block space-y-1">
            <span className="text-xs text-muted-foreground">Display name</span>
            <Input
              data-testid="agent-general-display-name"
              value={draft.displayName}
              onChange={(e) =>
                setDraft((d) => ({ ...d, displayName: e.target.value }))
              }
            />
          </label>

          <label className="block space-y-1">
            <span className="text-xs text-muted-foreground">Description</span>
            <textarea
              data-testid="agent-general-description"
              className="min-h-[96px] w-full rounded-md border border-input bg-background p-2 text-sm shadow-sm focus-visible:outline-none focus-visible:ring-1 focus-visible:ring-ring"
              value={draft.description}
              onChange={(e) =>
                setDraft((d) => ({ ...d, description: e.target.value }))
              }
            />
          </label>

          <div className="grid grid-cols-1 gap-3 sm:grid-cols-2">
            <label className="block space-y-1">
              <span className="text-xs text-muted-foreground">Role</span>
              <Input
                data-testid="agent-general-role"
                value={draft.role}
                placeholder="e.g. backend-engineer"
                onChange={(e) =>
                  setDraft((d) => ({ ...d, role: e.target.value }))
                }
              />
            </label>

            <label className="block space-y-1">
              <span className="text-xs text-muted-foreground">Specialty</span>
              <Input
                data-testid="agent-general-specialty"
                value={draft.specialty}
                placeholder="e.g. reviewer"
                onChange={(e) =>
                  setDraft((d) => ({ ...d, specialty: e.target.value }))
                }
              />
            </label>

            <label className="block space-y-1">
              <span className="text-xs text-muted-foreground">
                Model (hint)
              </span>
              <Input
                data-testid="agent-general-model"
                value={draft.model}
                placeholder="e.g. claude-3-5-sonnet-latest"
                onChange={(e) =>
                  setDraft((d) => ({ ...d, model: e.target.value }))
                }
              />
            </label>

            <label className="block space-y-1">
              <span className="text-xs text-muted-foreground">
                Execution mode
              </span>
              <select
                data-testid="agent-general-execution-mode"
                className="h-9 w-full rounded-md border border-input bg-background px-2 text-sm shadow-sm focus-visible:outline-none focus-visible:ring-1 focus-visible:ring-ring"
                value={draft.executionMode}
                onChange={(e) =>
                  setDraft((d) => ({
                    ...d,
                    executionMode: e.target.value as AgentExecutionMode,
                  }))
                }
              >
                {EXECUTION_MODES.map((m) => (
                  <option key={m} value={m}>
                    {m}
                  </option>
                ))}
              </select>
            </label>
          </div>

          <label className="flex items-center gap-2">
            <input
              type="checkbox"
              data-testid="agent-general-enabled"
              className="h-4 w-4 rounded border border-input"
              checked={draft.enabled}
              onChange={(e) =>
                setDraft((d) => ({ ...d, enabled: e.target.checked }))
              }
            />
            <span className="text-sm">
              Enabled — orchestration strategies skip this agent when off.
            </span>
          </label>

          <div className="flex items-center gap-2 pt-2">
            <Button
              onClick={() => saveMutation.mutate()}
              disabled={!dirty || saveMutation.isPending}
              data-testid="agent-general-save"
            >
              {saveMutation.isPending ? "Saving…" : "Save"}
            </Button>
            {dirty && (
              <Button
                variant="outline"
                onClick={() => setDraft(persisted)}
                disabled={saveMutation.isPending}
                data-testid="agent-general-revert"
              >
                Revert
              </Button>
            )}
          </div>
        </CardContent>
      </Card>

      <AgentExpertisePanel agentId={agentId} />
    </div>
  );
}
