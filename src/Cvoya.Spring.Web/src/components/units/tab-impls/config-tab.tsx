"use client";

// Unified Config tab (canonical-tabs.md § 5.11, #2254).
//
// One canonical control drives Tenant × Config, Unit × Config, and
// Agent × Config. The shared chrome (sub-tab strip + URL ownership +
// `<Tabs>` primitive) lives here; subject-specific sub-tabs gate by
// `kind`. Each per-subject wrapper (`tenant-config.tsx`,
// `unit-config.tsx`, `agent-config.tsx`) is a thin kind-guarded
// delegator that mounts this component.
//
// Sub-tab catalog per subject (canonical-tabs.md § 5.11):
//
//   Tenant — Secrets, Budget, Cloning. Bodies reuse the existing
//   `<TenantDefaultsPanel>`, `<BudgetPanel>`, and `<CloningPolicyPanel>`
//   so `/settings` and Tenant × Config render the same component bodies.
//   Tenant has no General sub-tab: the only editable tenant field
//   (display name) lives on the platform-admin surface, not the
//   tenant-operator Config surface.
//
//   Unit — General, Boundary, Execution, Instructions, Connector,
//   Tools, Secrets, Budget, Debug. #2331 added General as the first
//   tab (display name / description / model hint / color, plus the
//   expertise editor folded in) and retired the standalone Expertise
//   sub-tab. #2337 Sub D renamed the legacy Skills sub-tab to Tools
//   and replaced its body with the new three-tier `<ToolsPanel>`
//   (platform / connector / image).
//
//   Agent — General, Execution, Instructions, Budget, Connector,
//   Tools, Secrets, Debug. Same #2331 promotion (General first,
//   Expertise folded in) for the agent surface. The agent General
//   tab additionally exposes role, specialty, the enabled toggle, and
//   the execution-mode selector — none of which were previously
//   editable post-creation in the portal. #2337 Sub D renamed the
//   legacy Skills sub-tab to Tools — the top-level subject-view
//   Skills tab is a different concept and is unchanged.
//
// URL contract — `?tab=Config&subtab=<name>` on every subject. Writes
// go through `window.history.replaceState` + the Explorer URL-change
// event so deep links round-trip without triggering a full App Router
// navigation on every sub-tab click. Reads run through
// `useSyncExternalStore` so the component reacts to outer ?node / ?tab
// changes dispatched by `app/units/page.tsx` identically to its own
// writes.

import { useCallback, useSyncExternalStore } from "react";
import { Link2, Settings } from "lucide-react";

import { AgentBudgetPanel } from "@/components/agents/agent-budget-panel";
import { UnitBudgetPanel } from "@/components/units/unit-budget-panel";
import { AgentExecutionPanel } from "@/components/agents/tab-impls/execution-panel";
import { AgentOverridesPanel } from "@/components/settings/agent-overrides-panel";
import { BudgetPanel } from "@/components/settings/budget-panel";
import { CloningPolicyPanel } from "@/components/settings/cloning-policy-panel";
import { TenantDefaultsPanel } from "@/components/settings/tenant-defaults-panel";
import { AgentGeneralPanel } from "@/components/units/tab-impls/agent-general-panel";
import { BoundaryTab } from "@/components/units/tab-impls/boundary-tab";
import { ConnectorTab } from "@/components/units/tab-impls/connector-tab";
import { ExecutionTab } from "@/components/units/tab-impls/execution-tab";
import { InstructionsPanel } from "@/components/units/tab-impls/instructions-panel";
import { SecretsTab } from "@/components/units/tab-impls/secrets-tab";
import { ToolsPanel } from "@/components/units/tab-impls/tools-panel";
import { UnitGeneralPanel } from "@/components/units/tab-impls/unit-general-panel";
import {
  Tabs,
  TabsContent,
  TabsList,
  TabsTrigger,
} from "@/components/ui/tabs";
import {
  dispatchExplorerUrlChange,
  getExplorerUrlSnapshot,
  getServerExplorerUrlSnapshot,
  subscribeExplorerUrl,
} from "@/lib/explorer-url";

/** Subjects the unified Config tab can be driven by. */
export type ConfigSubjectKind = "Tenant" | "Unit" | "Agent";

export interface ConfigTabProps {
  /** Subject kind — drives the sub-tab catalog and per-panel body. */
  kind: ConfigSubjectKind;
  /** Stable id of the subject (tenant id, unit id, or agent id). */
  id: string;
  /**
   * For Agent only: the id of the agent's owning unit, when known. The
   * `<AgentExecutionPanel>` overlays inherited defaults from the owning
   * unit and accepts `null` when no parent is known — the parent lookup
   * lives in `useAgent(id)` and is the caller's responsibility because
   * registering a useAgent hook here would make the canonical component
   * fire that query for Tenant / Unit subjects too.
   */
  parentUnitId?: string | null;
  /**
   * For Agent only: the raw status payload surfaced inside the Debug
   * sub-tab. Same caller-side reason — the canonical component does not
   * own the useAgent hook so Tenant + Unit don't pay for an unused
   * agent-detail query.
   */
  status?: unknown;
}

// ---------------------------------------------------------------------------
// Sub-tab catalog. Each entry is the literal label rendered in the strip;
// the order in the array is the render order. Subject-specific catalogs
// gate by `kind` — sub-tabs that don't apply to a subject are not
// rendered (no "disabled" tabs) and not addressable via `?subtab=`.
// ---------------------------------------------------------------------------

const TENANT_SUBTABS = ["Secrets", "Budget", "Cloning"] as const;
const UNIT_SUBTABS = [
  "General",
  "Boundary",
  "Execution",
  "Instructions",
  "Connector",
  "Tools",
  "Secrets",
  "Budget",
  "Debug",
] as const;
const AGENT_SUBTABS = [
  "General",
  "Execution",
  "Instructions",
  "Budget",
  "Connector",
  "Tools",
  "Secrets",
  "Debug",
] as const;

type TenantSubTab = (typeof TENANT_SUBTABS)[number];
type UnitSubTab = (typeof UNIT_SUBTABS)[number];
type AgentSubTab = (typeof AGENT_SUBTABS)[number];
type SubTab = TenantSubTab | UnitSubTab | AgentSubTab;

function subtabsFor(kind: ConfigSubjectKind): readonly SubTab[] {
  switch (kind) {
    case "Tenant":
      return TENANT_SUBTABS;
    case "Unit":
      return UNIT_SUBTABS;
    case "Agent":
      return AGENT_SUBTABS;
  }
}

function parseSubTab(
  raw: string | null,
  catalog: readonly SubTab[],
): SubTab {
  const fallback = catalog[0];
  if (!raw) return fallback;
  const match = catalog.find((s) => s === raw);
  return match ?? fallback;
}

// ---------------------------------------------------------------------------
// Canonical Config tab body. The sub-tab strip + URL ownership are
// shared across all three subjects; the per-sub-tab body switches on
// `kind` + `subtab`.
// ---------------------------------------------------------------------------

export function ConfigTab({
  kind,
  id,
  parentUnitId = null,
  status,
}: ConfigTabProps) {
  const catalog = subtabsFor(kind);

  // Read the current URL via useSyncExternalStore so this component
  // reacts to both its own replaceState writes and outer ?node/?tab
  // changes dispatched by page.tsx — without triggering App Router
  // navigations on every sub-tab click. Same pattern as the legacy
  // `unit-config.tsx`.
  const search = useSyncExternalStore(
    subscribeExplorerUrl,
    getExplorerUrlSnapshot,
    getServerExplorerUrlSnapshot,
  );
  const activeSubTab = parseSubTab(
    new URLSearchParams(search).get("subtab"),
    catalog,
  );

  // Reads window.location.search at call time so the closure never
  // captures a stale snapshot; no deps for the same reason.
  const setActiveSubTab = useCallback((next: string) => {
    const params = new URLSearchParams(window.location.search);
    params.set("subtab", next);
    const qs = params.toString();
    window.history.replaceState(
      null,
      "",
      qs
        ? `${window.location.pathname}?${qs}`
        : window.location.pathname,
    );
    dispatchExplorerUrlChange();
  }, []);

  const description = describeSubject(kind);
  const testId = subjectTestId(kind);

  return (
    <div className="space-y-4" data-testid={testId}>
      <header className="flex items-center gap-2 text-sm text-muted-foreground">
        <Settings className="h-4 w-4" aria-hidden="true" />
        <span>{description}</span>
      </header>
      <Tabs
        defaultValue={catalog[0]}
        value={activeSubTab}
        onValueChange={setActiveSubTab}
      >
        <TabsList aria-label={`${kind} configuration sections`}>
          {catalog.map((s) => (
            <TabsTrigger key={s} value={s}>
              {s}
            </TabsTrigger>
          ))}
        </TabsList>

        {/* Tenant sub-tabs */}
        {kind === "Tenant" && (
          <>
            <TabsContent value="Secrets" className="space-y-2">
              <TenantDefaultsPanel />
            </TabsContent>
            <TabsContent value="Budget" className="space-y-2">
              <BudgetPanel />
            </TabsContent>
            <TabsContent value="Cloning" className="space-y-2">
              <CloningPolicyPanel />
            </TabsContent>
          </>
        )}

        {/* Unit sub-tabs — General + Boundary/Execution/Instructions/Connector/Skills/Secrets + Budget + Debug.
            Expertise was folded into General under #2331; the legacy Expertise sub-tab is retired. */}
        {kind === "Unit" && (
          <>
            <TabsContent value="General" className="space-y-2">
              <UnitGeneralPanel unitId={id} />
            </TabsContent>
            <TabsContent value="Boundary" className="space-y-2">
              <BoundaryTab unitId={id} />
            </TabsContent>
            <TabsContent value="Execution" className="space-y-2">
              <ExecutionTab unitId={id} />
            </TabsContent>
            <TabsContent value="Instructions" className="space-y-2">
              <InstructionsPanel kind="Unit" id={id} />
            </TabsContent>
            <TabsContent value="Connector" className="space-y-2">
              <ConnectorTab unitId={id} />
            </TabsContent>
            <TabsContent value="Tools" className="space-y-2">
              <ToolsPanel kind="Unit" id={id} />
            </TabsContent>
            <TabsContent value="Secrets" className="space-y-2">
              <SecretsTab unitId={id} />
            </TabsContent>
            <TabsContent value="Budget" className="space-y-2">
              <UnitBudgetPanel unitId={id} />
            </TabsContent>
            <TabsContent value="Debug" className="space-y-2">
              <DebugSection
                status={null}
                emptyCopy="Unit-scope runtime status is not surfaced today. Run `spring unit status` for the latest raw payload."
              />
            </TabsContent>
          </>
        )}

        {/* Agent sub-tabs — General + Execution/Instructions/Budget/Connector/Skills/Secrets + Debug.
            Expertise was folded into General under #2331; the legacy Expertise sub-tab is retired. */}
        {kind === "Agent" && (
          <>
            <TabsContent value="General" className="space-y-2">
              <AgentGeneralPanel agentId={id} />
            </TabsContent>
            <TabsContent value="Execution" className="space-y-2">
              <AgentExecutionPanel agentId={id} parentUnitId={parentUnitId} />
            </TabsContent>
            <TabsContent value="Instructions" className="space-y-2">
              <InstructionsPanel
                kind="Agent"
                id={id}
                parentUnitId={parentUnitId}
              />
            </TabsContent>
            <TabsContent value="Budget" className="space-y-2">
              <AgentBudgetPanel agentId={id} />
            </TabsContent>
            <TabsContent value="Connector" className="space-y-2">
              <AgentConnectorInheritedView parentUnitId={parentUnitId} />
            </TabsContent>
            <TabsContent value="Tools" className="space-y-2">
              <ToolsPanel kind="Agent" id={id} parentUnitId={parentUnitId} />
            </TabsContent>
            <TabsContent value="Secrets" className="space-y-2">
              <AgentOverridesPanel
                agentId={id}
                parentUnitId={parentUnitId ?? undefined}
              />
            </TabsContent>
            <TabsContent value="Debug" className="space-y-2">
              <DebugSection status={status} />
            </TabsContent>
          </>
        )}
      </Tabs>
    </div>
  );
}

function describeSubject(kind: ConfigSubjectKind): string {
  switch (kind) {
    case "Tenant":
      return (
        "Tenant-default credentials, daily budget, and cloning policy. " +
        "The same bodies render on /settings — one canonical home, two access paths."
      );
    case "Unit":
      return (
        "General metadata (display name, description, color, expertise) and the " +
        "boundary, execution defaults, instructions, connector, tools, secrets, " +
        "and budget for this unit. Each section mirrors the matching " +
        "`spring unit …` CLI subcommand."
      );
    case "Agent":
      return (
        "General metadata (display name, description, role, specialty, expertise) " +
        "plus execution defaults, instructions, daily budget, connector, tools, " +
        "and secret overrides for this agent. Mirrors the matching " +
        "`spring agent …` CLI subcommands. Initiative lives on the Policies tab."
      );
  }
}

function subjectTestId(kind: ConfigSubjectKind): string {
  return kind === "Tenant"
    ? "tab-tenant-config"
    : kind === "Unit"
      ? "tab-unit-config"
      : "tab-agent-config";
}

// ---------------------------------------------------------------------------
// Collapsible Advanced/Debug sub-tab body. Mirrors the legacy
// `agent-config.tsx` debug section — the same raw-status JSON rendered
// in a `<details>` block so screen readers and keyboard users toggle it
// natively. Defaulted to collapsed so the panel reads as a clean
// placeholder when opened; expand it to reveal the payload.
// ---------------------------------------------------------------------------

function DebugSection({
  status,
  emptyCopy = "(no status reported)",
}: {
  status: unknown;
  emptyCopy?: string;
}) {
  // Stringify defensively — `status` is typed as `JsonElement | null`,
  // and the generated schema leaves it widely open. The replacer
  // fallback keeps us safe from circular refs (none are expected
  // server-side, but defence in depth is cheap).
  const pretty = (() => {
    if (status == null) return emptyCopy;
    try {
      return JSON.stringify(status, null, 2);
    } catch {
      return String(status);
    }
  })();

  return (
    <section aria-label="Debug">
      <details
        className="group rounded-md border border-border"
        data-testid="agent-debug-section"
      >
        <summary className="flex cursor-pointer items-center gap-2 px-3 py-2 text-sm font-medium">
          <span>Debug</span>
          <span className="text-xs text-muted-foreground">
            Raw status payload
          </span>
        </summary>
        <pre
          className="max-h-96 overflow-auto whitespace-pre-wrap border-t border-border bg-muted/40 p-3 font-mono text-xs"
          data-testid="agent-debug-status"
        >
          {pretty}
        </pre>
      </details>
    </section>
  );
}

// ---------------------------------------------------------------------------
// Placeholder bodies for sub-tabs where the canonical home applies but
// the underlying wire is not in place yet. Same precedent as
// `equipped-skills-tab.tsx` (Unit Skills, #2276) and `deployment-tab.tsx`
// (Unit Deployment, #2274): the canonical tab position is honored even
// when the body is a CLI deep-link.
// ---------------------------------------------------------------------------

function AgentConnectorInheritedView({
  parentUnitId,
}: {
  parentUnitId?: string | null;
}) {
  const unitLink = parentUnitId
    ? `?node=${parentUnitId}&tab=Config&subtab=Connector`
    : null;

  return (
    <div
      className="space-y-3 rounded-lg border border-border bg-muted/10 p-6 text-sm"
      data-testid="tab-agent-config-connector-inherited"
    >
      <div className="flex items-start gap-2">
        <Link2
          className="mt-0.5 h-5 w-5 shrink-0 text-muted-foreground"
          aria-hidden="true"
        />
        <div className="space-y-2">
          <p className="font-medium">Connector binding — inherited from owning unit</p>
          <p className="text-xs text-muted-foreground">
            Connector binding is a unit-only concept. An agent inherits
            connector reachability from the unit that owns it. To configure
            which external events reach this agent, edit the{" "}
            {unitLink ? (
              <a href={unitLink} className="underline">
                owning unit&apos;s Connector
              </a>
            ) : (
              "owning unit's Connector"
            )}{" "}
            sub-tab.
          </p>
          <p className="text-xs text-muted-foreground">
            To view the current binding:{" "}
            <code className="rounded bg-muted px-1 py-0.5 font-mono text-xs">
              spring unit connector show &lt;unit-id&gt;
            </code>
          </p>
        </div>
      </div>
    </div>
  );
}
