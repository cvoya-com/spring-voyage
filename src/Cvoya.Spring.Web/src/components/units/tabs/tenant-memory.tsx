"use client";

// Tenant Memory tab (EXP-tab-tenant, umbrella #815 §4 / §12).
//
// The v2.0 backend ships `/api/v1/units/{id}/memories` +
// `/api/v1/agents/{id}/memories` but NOT a tenant-scoped memory
// endpoint. This tab therefore renders a static empty state that
// matches the unit/agent Memory tabs' copy so operators see
// a consistent "lands in v2.1" message across scopes.

import { Brain } from "lucide-react";

import { registerTab, type TabContentProps } from "./index";

function TenantMemoryTab({ node }: TabContentProps) {
  if (node.kind !== "Tenant") return null;

  return (
    <div
      data-testid="tab-tenant-memory-empty"
      className="rounded-lg border border-dashed border-border bg-muted/30 p-6 text-center"
    >
      <Brain className="mx-auto h-6 w-6 text-muted-foreground" aria-hidden="true" />
      <p className="mt-2 text-sm font-medium">No tenant memory yet</p>
      <p className="mt-1 text-xs text-muted-foreground">
        Tenant memory lands alongside unit + agent memory in v2.1.
      </p>
    </div>
  );
}

registerTab("Tenant", "Memory", TenantMemoryTab);

export default TenantMemoryTab;
