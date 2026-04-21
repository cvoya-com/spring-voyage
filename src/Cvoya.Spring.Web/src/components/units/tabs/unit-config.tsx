"use client";

// Unit Config tab (EXP-tab-unit-config, umbrella #815 §4).
//
// The Explorer consolidates the legacy `/units/[id]` tabs that all
// configure how a unit executes — Boundary, Execution, Connector,
// Skills, Secrets — into a single "Config" surface. Each legacy panel
// is reused verbatim so behaviour, hooks, and tests stay shared with
// the retiring route.
//
// Sub-tabs (QUALITY-unit-config-subtabs, #904). Five panels stacked
// vertically produced a long scrollable wall on mid-size units, so
// the panels are wrapped in the canonical `<Tabs>` primitive. Active
// sub-tab lives in the URL as `?subtab=<name>` alongside the existing
// `?node=` + `?tab=Config` — this keeps the sub-tab state private to
// the Config surface (DetailPane's `tab` prop is still just the outer
// tab name) while letting deep links round-trip through refresh.

import { Settings } from "lucide-react";
import { useCallback } from "react";
import { useRouter, useSearchParams } from "next/navigation";

import {
  Tabs,
  TabsContent,
  TabsList,
  TabsTrigger,
} from "@/components/ui/tabs";
import { UnitExpertisePanel } from "@/components/expertise/unit-expertise-panel";
import { BoundaryTab } from "@/components/units/tab-impls/boundary-tab";
import { ConnectorTab } from "@/components/units/tab-impls/connector-tab";
import { ExecutionTab } from "@/components/units/tab-impls/execution-tab";
import { SecretsTab } from "@/components/units/tab-impls/secrets-tab";
import { SkillsTab } from "@/components/units/tab-impls/skills-tab";

import { registerTab, type TabContentProps } from "./index";

// The ordered list of sub-tabs rendered inside the Config surface. The
// first entry is the default (used when the URL has no `?subtab=` or
// carries an unknown value). "Expertise" lands here (not in its own
// Overview slot) because editing the list is the lowest-frequency
// Config action and the Overview tab hosts a read-only summary card
// with a Manage link that deep-links straight to this sub-tab
// (EXP-tab-unit-overview-expertise-card, #936).
const SUBTABS = [
  "Boundary",
  "Execution",
  "Connector",
  "Skills",
  "Secrets",
  "Expertise",
] as const;
type SubTab = (typeof SUBTABS)[number];
const DEFAULT_SUBTAB: SubTab = SUBTABS[0];

function parseSubTab(raw: string | null): SubTab {
  if (!raw) return DEFAULT_SUBTAB;
  const match = SUBTABS.find((s) => s === raw);
  return match ?? DEFAULT_SUBTAB;
}

function UnitConfigTab({ node }: TabContentProps) {
  const router = useRouter();
  const searchParams = useSearchParams();
  const activeSubTab = parseSubTab(searchParams.get("subtab"));

  // Mirror sub-tab selection into `?subtab=` while preserving the
  // existing `?node=` + `?tab=Config` so the Explorer's deep-link
  // contract still round-trips. Hook runs before the `kind` check —
  // rules-of-hooks demands stable call order — and only writes the
  // URL from inside a click handler.
  const setActiveSubTab = useCallback(
    (next: string) => {
      const params = new URLSearchParams(searchParams.toString());
      params.set("subtab", next);
      const qs = params.toString();
      router.replace(qs ? `?${qs}` : "?", { scroll: false });
    },
    [router, searchParams],
  );

  if (node.kind !== "Unit") return null;

  return (
    <div className="space-y-4" data-testid="tab-unit-config">
      <header className="flex items-center gap-2 text-sm text-muted-foreground">
        <Settings className="h-4 w-4" aria-hidden="true" />
        <span>
          Boundary, execution defaults, connector, skills, and secrets for
          this unit. Each section mirrors the matching `spring unit …` CLI
          subcommand.
        </span>
      </header>
      <Tabs
        defaultValue={DEFAULT_SUBTAB}
        value={activeSubTab}
        onValueChange={setActiveSubTab}
      >
        <TabsList aria-label="Unit configuration sections">
          {SUBTABS.map((s) => (
            <TabsTrigger key={s} value={s}>
              {s}
            </TabsTrigger>
          ))}
        </TabsList>

        <TabsContent value="Boundary" className="space-y-2">
          <BoundaryTab unitId={node.id} />
        </TabsContent>
        <TabsContent value="Execution" className="space-y-2">
          <ExecutionTab unitId={node.id} />
        </TabsContent>
        <TabsContent value="Connector" className="space-y-2">
          <ConnectorTab unitId={node.id} />
        </TabsContent>
        <TabsContent value="Skills" className="space-y-2">
          <SkillsTab unitId={node.id} />
        </TabsContent>
        <TabsContent value="Secrets" className="space-y-2">
          <SecretsTab unitId={node.id} />
        </TabsContent>
        <TabsContent value="Expertise" className="space-y-2">
          <UnitExpertisePanel unitId={node.id} />
        </TabsContent>
      </Tabs>
    </div>
  );
}

registerTab("Unit", "Config", UnitConfigTab);

export default UnitConfigTab;
