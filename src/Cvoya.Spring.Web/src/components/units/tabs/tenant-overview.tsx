"use client";

// Tenant Overview tab (EXP-tab-tenant, umbrella #815 §4).
//
// Landing page for the tenant root. Renders a grid of UnitCards for
// the top-level units under the tenant so operators can teleport into
// any unit with one click. The card's tab chips dispatch through
// `useExplorerSelection().dispatchSelect(id)` so the Explorer jumps
// to the selected node in-place instead of forcing a router round-trip.

import { Layers } from "lucide-react";

import { UnitCard } from "@/components/cards/unit-card";

import { useExplorerSelection } from "../explorer-selection-context";
import type { TenantNode, UnitNode } from "../aggregate";
import { aggregate } from "../aggregate";

import { registerTab, type TabContentProps } from "./index";

function mapStatus(status: string): string {
  switch (status) {
    case "running":
      return "Running";
    case "starting":
      return "Starting";
    case "paused":
    case "stopped":
      return "Stopped";
    case "error":
      return "Error";
    default:
      return "Draft";
  }
}

function TenantOverviewTab({ node }: TabContentProps) {
  // Hook runs unconditionally — registry guarantees `kind === "Tenant"`.
  const { dispatchSelect } = useExplorerSelection();
  if (node.kind !== "Tenant") return null;
  const tenant = node as TenantNode;

  const units: UnitNode[] = (tenant.children ?? []).filter(
    (c): c is UnitNode => c.kind === "Unit",
  );

  if (units.length === 0) {
    return (
      <div
        data-testid="tab-tenant-overview-empty"
        className="rounded-lg border border-dashed border-border bg-muted/30 p-6 text-center"
      >
        <Layers className="mx-auto h-6 w-6 text-muted-foreground" aria-hidden="true" />
        <p className="mt-2 text-sm font-medium">No units yet</p>
        <p className="mt-1 text-xs text-muted-foreground">
          Create a unit from the Dashboard or via{" "}
          <code className="rounded bg-muted px-1 py-0.5 text-xs">
            spring unit create
          </code>
          .
        </p>
      </div>
    );
  }

  return (
    <div
      className="grid grid-cols-1 gap-3 sm:grid-cols-2 lg:grid-cols-3"
      data-testid="tab-tenant-overview"
    >
      {units.map((u) => {
        const roll = aggregate(u);
        return (
          <UnitCard
            key={u.id}
            unit={{
              name: u.id,
              displayName: u.name,
              registeredAt: new Date().toISOString(),
              status: mapStatus(u.status),
              cost: roll.cost,
            }}
            onOpenTab={(id) => dispatchSelect(id)}
          />
        );
      })}
    </div>
  );
}

registerTab("Tenant", "Overview", TenantOverviewTab);

export default TenantOverviewTab;
