"use client";

// Unit Config tab (EXP-tab-unit-config, umbrella #815 §4).
//
// The Explorer consolidates the legacy `/units/[id]` tabs that all
// configure how a unit executes — Boundary, Execution, Connector,
// Secrets, Skills — into a single "Config" surface. Each legacy panel
// is reused verbatim so behaviour, hooks, and tests stay shared with
// the retiring route until `DEL-units-id` lands.

import { Settings } from "lucide-react";

import { BoundaryTab } from "@/app/units/[id]/boundary-tab";
import { ConnectorTab } from "@/app/units/[id]/connector-tab";
import { ExecutionTab } from "@/app/units/[id]/execution-tab";
import { SecretsTab } from "@/app/units/[id]/secrets-tab";
import { SkillsTab } from "@/app/units/[id]/skills-tab";

import { registerTab, type TabContentProps } from "./index";

function UnitConfigTab({ node }: TabContentProps) {
  if (node.kind !== "Unit") return null;

  return (
    <div className="space-y-6" data-testid="tab-unit-config">
      <header className="flex items-center gap-2 text-sm text-muted-foreground">
        <Settings className="h-4 w-4" aria-hidden="true" />
        <span>
          Boundary, execution defaults, connector, secrets, and skills for
          this unit. Each section mirrors the matching `spring unit …` CLI
          subcommand.
        </span>
      </header>
      <ConfigSection title="Boundary">
        <BoundaryTab unitId={node.id} />
      </ConfigSection>
      <ConfigSection title="Execution">
        <ExecutionTab unitId={node.id} />
      </ConfigSection>
      <ConfigSection title="Connector">
        <ConnectorTab unitId={node.id} />
      </ConfigSection>
      <ConfigSection title="Skills">
        <SkillsTab unitId={node.id} />
      </ConfigSection>
      <ConfigSection title="Secrets">
        <SecretsTab unitId={node.id} />
      </ConfigSection>
    </div>
  );
}

function ConfigSection({
  title,
  children,
}: {
  title: string;
  children: React.ReactNode;
}) {
  return (
    <section className="space-y-2" aria-label={title}>
      <h3 className="text-sm font-medium">{title}</h3>
      {children}
    </section>
  );
}

registerTab("Unit", "Config", UnitConfigTab);

export default UnitConfigTab;
