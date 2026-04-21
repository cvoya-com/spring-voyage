"use client";

// Unit Overview tab (EXP-tab-unit-overview, umbrella #815 §4).
//
// Shows stat tiles rolled up from the subtree via `aggregate(node)`:
// agents count, sub-unit count, 24h cost, 24h msgs, and the worst
// status in the subtree. The tiles deliberately stay lightweight — the
// richer drill-downs belong on the dedicated Agents / Activity / Policies
// tabs, which each ship their own registered content.

import { Activity, Bot, DollarSign, Layers, MessagesSquare } from "lucide-react";

import { Badge } from "@/components/ui/badge";
import { StatCard } from "@/components/stat-card";
import { formatCost } from "@/lib/utils";

import { aggregate, type UnitNode } from "../aggregate";
import { UnitOverviewExpertiseCard } from "../unit-overview-expertise-card";

import { registerTab, type TabContentProps } from "./index";

function UnitOverviewTab({ node }: TabContentProps) {
  if (node.kind !== "Unit") return null;
  const unit = node as UnitNode;
  const roll = aggregate(unit);

  return (
    <div className="space-y-4" data-testid="tab-unit-overview">
      {unit.desc ? (
        <p className="text-sm text-muted-foreground">{unit.desc}</p>
      ) : null}
      <div className="grid grid-cols-2 gap-3 sm:grid-cols-3 lg:grid-cols-5">
        <StatCard
          label="Agents"
          value={roll.agents}
          icon={<Bot className="h-4 w-4" aria-hidden="true" />}
        />
        <StatCard
          label="Sub-units"
          value={Math.max(0, roll.units - 1)}
          icon={<Layers className="h-4 w-4" aria-hidden="true" />}
        />
        <StatCard
          label="Cost (24h)"
          value={formatCost(roll.cost)}
          icon={<DollarSign className="h-4 w-4" aria-hidden="true" />}
        />
        <StatCard
          label="Messages (24h)"
          value={roll.msgs.toLocaleString()}
          icon={<MessagesSquare className="h-4 w-4" aria-hidden="true" />}
        />
        <StatCard
          label="Worst status"
          value={roll.worst}
          icon={<Activity className="h-4 w-4" aria-hidden="true" />}
        />
      </div>
      <div className="text-xs text-muted-foreground">
        Subtree roll-ups include this unit and every descendant. See the{" "}
        <Badge variant="outline">Agents</Badge> and{" "}
        <Badge variant="outline">Activity</Badge> tabs for drill-downs.
      </div>

      <UnitOverviewExpertiseCard unitId={unit.id} />
    </div>
  );
}

registerTab("Unit", "Overview", UnitOverviewTab);

export default UnitOverviewTab;
