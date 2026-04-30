"use client";

// Tenant-wide cloning-policy panel (Settings hub, #534 / PR-PLAT-CLONE-1).
//
// Read-only summary of the persistent cloning policy that the enforcer
// consults on every clone request. The panel exposes the key constraints
// so operators can confirm the tenant default without reaching for the CLI.
// Editing still goes through `spring agent clone policy set --scope tenant`
// or the dedicated CLI — write support is tracked as a polish follow-up.
//
// Wire: GET /api/v1/tenant/cloning-policy → AgentCloningPolicyResponse.

import { Copy } from "lucide-react";

import { Badge } from "@/components/ui/badge";
import { Skeleton } from "@/components/ui/skeleton";
import { useTenantCloningPolicy } from "@/lib/api/queries";

export function CloningPolicyPanel() {
  const { data: policy, isPending, isError } = useTenantCloningPolicy();

  if (isPending) {
    return (
      <Skeleton
        className="h-20 w-full"
        data-testid="settings-cloning-policy-loading"
      />
    );
  }

  if (isError || policy === null) {
    return (
      <p
        className="text-xs text-muted-foreground"
        data-testid="settings-cloning-policy-error"
      >
        Could not load the tenant cloning policy. Check your connection or
        retry.
      </p>
    );
  }

  // Detect if the policy is effectively empty (no constraints set).
  const isEmpty =
    !policy.allowedPolicies?.length &&
    !policy.allowedAttachmentModes?.length &&
    policy.maxClones == null &&
    policy.maxDepth == null &&
    policy.budget == null;

  return (
    <div
      className="space-y-3 text-sm"
      data-testid="settings-cloning-policy-panel"
    >
      {isEmpty ? (
        <p className="text-xs text-muted-foreground">
          No tenant-wide cloning constraints are set. All cloning policies and
          attachment modes are permitted by default.
        </p>
      ) : (
        <dl className="space-y-2 text-xs">
          {policy.allowedPolicies && policy.allowedPolicies.length > 0 && (
            <div>
              <dt className="font-medium text-foreground">Allowed policies</dt>
              <dd className="mt-0.5 flex flex-wrap gap-1">
                {policy.allowedPolicies.map((p) => (
                  <Badge key={p} variant="secondary" className="font-mono text-[11px]">
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
              <dt className="font-medium text-foreground">Max concurrent clones</dt>
              <dd className="font-mono tabular-nums">{policy.maxClones}</dd>
            </div>
          )}
          {policy.maxDepth != null && (
            <div className="flex items-center gap-2">
              <dt className="font-medium text-foreground">Max recursion depth</dt>
              <dd className="font-mono tabular-nums">
                {policy.maxDepth === 0 ? "0 (no recursive cloning)" : policy.maxDepth}
              </dd>
            </div>
          )}
          {policy.budget != null && (
            <div className="flex items-center gap-2">
              <dt className="font-medium text-foreground">Per-clone budget</dt>
              <dd className="font-mono tabular-nums">${policy.budget.toFixed(2)}</dd>
            </div>
          )}
        </dl>
      )}
      <p className="text-xs text-muted-foreground">
        Edit via{" "}
        <code className="font-mono text-[11px]">
          spring agent clone policy set --scope tenant
        </code>
        .
      </p>
    </div>
  );
}

export function CloningPolicyIcon() {
  return <Copy className="h-4 w-4" aria-hidden="true" />;
}
