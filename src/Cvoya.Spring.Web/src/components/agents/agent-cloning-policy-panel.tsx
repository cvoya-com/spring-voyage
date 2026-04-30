"use client";

// Agent-scoped cloning policy panel (#534 / PR-PLAT-CLONE-1).
//
// Read-only summary of the persistent cloning policy for a single
// agent. When no agent-specific policy is set the server returns the
// empty-policy shape (not a 404), which the panel renders as
// "No agent-specific constraints — tenant default applies."
//
// Wire: GET /api/v1/tenant/agents/{id}/cloning-policy → AgentCloningPolicyResponse.

import { Badge } from "@/components/ui/badge";
import { Skeleton } from "@/components/ui/skeleton";
import { useAgentCloningPolicy } from "@/lib/api/queries";

export function AgentCloningPolicyPanel({ agentId }: { agentId: string }) {
  const {
    data: policy,
    isPending,
    isError,
  } = useAgentCloningPolicy(agentId);

  if (isPending) {
    return (
      <Skeleton
        className="h-16 w-full"
        data-testid="agent-cloning-policy-loading"
      />
    );
  }

  if (isError || policy === null) {
    return (
      <p
        className="text-xs text-muted-foreground"
        data-testid="agent-cloning-policy-error"
      >
        Could not load the cloning policy. Check your connection or retry.
      </p>
    );
  }

  const isEmpty =
    !policy.allowedPolicies?.length &&
    !policy.allowedAttachmentModes?.length &&
    policy.maxClones == null &&
    policy.maxDepth == null &&
    policy.budget == null;

  return (
    <div
      className="space-y-2 text-xs"
      data-testid="agent-cloning-policy-panel"
    >
      {isEmpty ? (
        <p className="text-muted-foreground">
          No agent-specific cloning constraints — the tenant-wide default
          policy applies.
        </p>
      ) : (
        <dl className="space-y-2">
          {policy.allowedPolicies && policy.allowedPolicies.length > 0 && (
            <div>
              <dt className="font-medium text-foreground">Allowed policies</dt>
              <dd className="mt-0.5 flex flex-wrap gap-1">
                {policy.allowedPolicies.map((p) => (
                  <Badge
                    key={p}
                    variant="secondary"
                    className="font-mono text-[11px]"
                  >
                    {p}
                  </Badge>
                ))}
              </dd>
            </div>
          )}
          {policy.allowedAttachmentModes &&
            policy.allowedAttachmentModes.length > 0 && (
              <div>
                <dt className="font-medium text-foreground">
                  Allowed attachment modes
                </dt>
                <dd className="mt-0.5 flex flex-wrap gap-1">
                  {policy.allowedAttachmentModes.map((m) => (
                    <Badge
                      key={m}
                      variant="secondary"
                      className="font-mono text-[11px]"
                    >
                      {m}
                    </Badge>
                  ))}
                </dd>
              </div>
            )}
          {policy.maxClones != null && (
            <div className="flex items-center gap-2">
              <dt className="font-medium text-foreground">
                Max concurrent clones
              </dt>
              <dd className="font-mono tabular-nums">{policy.maxClones}</dd>
            </div>
          )}
          {policy.maxDepth != null && (
            <div className="flex items-center gap-2">
              <dt className="font-medium text-foreground">
                Max recursion depth
              </dt>
              <dd className="font-mono tabular-nums">
                {policy.maxDepth === 0
                  ? "0 (no recursive cloning)"
                  : policy.maxDepth}
              </dd>
            </div>
          )}
          {policy.budget != null && (
            <div className="flex items-center gap-2">
              <dt className="font-medium text-foreground">Per-clone budget</dt>
              <dd className="font-mono tabular-nums">
                ${policy.budget.toFixed(2)}
              </dd>
            </div>
          )}
        </dl>
      )}
      <p className="text-muted-foreground">
        Edit via{" "}
        <code className="font-mono text-[11px]">
          spring agent clone policy set {agentId}
        </code>
        . The tenant-wide default is visible in{" "}
        <a href="/settings" className="text-primary hover:underline">
          Settings → Tenant cloning policy
        </a>
        .
      </p>
    </div>
  );
}
