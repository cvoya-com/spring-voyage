"use client";

// ToolsPanel — Config × Tools sub-tab body for Unit and Agent (#2337 Sub D).
//
// Renders the three-tier effective-tool set the server projects onto
// AgentResponse / UnitResponse.effectiveTools (resolver from Sub B
// #2335). Sections:
//
//   1. Platform   — collapsed <details>; lists every sv.* tool.
//   2. Connectors — one group per `connector:<slug>` provenance, with
//                   an "Enabled" / "Inherited from <unit>" badge based
//                   on whether the grant flowed in from a parent unit.
//   3. Image      — read-only list of tools the running image declared
//                   (image:<digest> provenance). The section header
//                   renders the image *tag* sourced from the server's
//                   `executionImage` field (#2348); the legacy
//                   digest-suffix derivation was dropped because the
//                   digest is opaque to operators and provenance carries
//                   per-tool noise that does not belong in a header.
//                   Empty-state copy makes it clear the image declares
//                   no custom tools.
//
// v0.1 ships at namespace granularity — there is no per-tool toggle.
// Per-tool deny inside a granted namespace is tracked as #2333 (v0.2).
//
// Inheritance overlay: connector sections whose grant is inherited
// from a parent unit render with `opacity-60` and surface a link back
// to the parent unit's Tools sub-tab so the operator can edit the
// binding in one click.

import { Link2, Sparkles, Wrench } from "lucide-react";

import { ApiErrorMessage } from "@/components/ui/api-error-message";
import { Badge } from "@/components/ui/badge";
import { useAgent, useUnit } from "@/lib/api/queries";
import type { EffectiveToolResponse } from "@/lib/api/types";

export type ToolsPanelSubjectKind = "Unit" | "Agent";

export interface ToolsPanelProps {
  /** Subject kind — drives the data hook + the per-section test ids. */
  kind: ToolsPanelSubjectKind;
  /** Stable id of the subject (unit id or agent id). */
  id: string;
  /**
   * For Agent only: id of the agent's owning unit, when known. Used to
   * link inherited connector sections back to the parent unit's
   * Tools sub-tab.
   */
  parentUnitId?: string | null;
}

interface ToolsView {
  /** Flat list of effective tools as projected by the server. */
  tools: readonly EffectiveToolResponse[];
  /**
   * Image tag sourced from the server's `executionImage` field (#2348)
   * — e.g. `acme/agent:v1.2`. `null` when neither the subject nor its
   * primary parent unit declares an image; the section header falls
   * back to the literal "Image" string in that case.
   */
  imageLabel: string | null;
}

export function ToolsPanel({ kind, id, parentUnitId = null }: ToolsPanelProps) {
  const view = kind === "Agent" ? <AgentToolsView id={id} parentUnitId={parentUnitId} />
                                : <UnitToolsView id={id} />;
  return view;
}

// ---------------------------------------------------------------------------
// Per-kind wrappers — own the data fetch and hand a uniform `ToolsView`
// to the body. Split out so the body stays oblivious to which hook
// fired, mirroring the `<EquippedSkillsTab>` bindings table from #2271.
// ---------------------------------------------------------------------------

function UnitToolsView({ id }: { id: string }) {
  const unitQuery = useUnit(id);

  if (unitQuery.isLoading) {
    return (
      <p
        role="status"
        aria-live="polite"
        className="text-sm text-muted-foreground"
        data-testid="tab-unit-tools-loading"
      >
        Loading tools…
      </p>
    );
  }

  if (unitQuery.error) {
    return (
      <div data-testid="tab-unit-tools-error">
        <ApiErrorMessage error={unitQuery.error} />
      </div>
    );
  }

  return (
    <ToolsBody
      testId="tab-unit-tools"
      tools={unitQuery.data?.effectiveTools ?? []}
      imageLabel={unitQuery.data?.executionImage ?? null}
      parentUnitId={null}
    />
  );
}

function AgentToolsView({
  id,
  parentUnitId,
}: {
  id: string;
  parentUnitId: string | null;
}) {
  const agentQuery = useAgent(id);

  if (agentQuery.isLoading) {
    return (
      <p
        role="status"
        aria-live="polite"
        className="text-sm text-muted-foreground"
        data-testid="tab-agent-tools-loading"
      >
        Loading tools…
      </p>
    );
  }

  if (agentQuery.error) {
    return (
      <div data-testid="tab-agent-tools-error">
        <ApiErrorMessage error={agentQuery.error} />
      </div>
    );
  }

  return (
    <ToolsBody
      testId="tab-agent-tools"
      tools={agentQuery.data?.agent.effectiveTools ?? []}
      imageLabel={agentQuery.data?.agent.executionImage ?? null}
      parentUnitId={parentUnitId}
    />
  );
}

// ---------------------------------------------------------------------------
// Shared body — renders the three sections off a flat tool list. Kept
// kind-agnostic so the data path can grow (e.g. live image-tag wiring)
// without invalidating the rendering logic.
// ---------------------------------------------------------------------------

interface ToolsBodyProps extends ToolsView {
  testId: string;
  parentUnitId: string | null;
}

function ToolsBody({ testId, tools, imageLabel, parentUnitId }: ToolsBodyProps) {
  const platform = tools.filter((t) => t.provenance === "platform");
  const connectorGroups = groupByConnector(tools);
  const image = tools.filter((t) => t.provenance.startsWith("image:"));
  // #2348: the section header renders the server-projected
  // `executionImage` tag (e.g. `acme/agent:v1.2`) when present and
  // falls back to the literal "Image" string when null. The previous
  // digest-suffix derivation that inferred a label from the
  // `image:<digest>` provenance is dropped — digests are opaque to
  // operators and provenance carries per-tool noise that does not
  // belong in a section header.
  const renderedImageLabel = imageLabel ?? "Image";

  return (
    <div className="space-y-4" data-testid={testId}>
      <p className="text-xs text-muted-foreground">
        Tools the platform, bound connectors, and the container image expose
        to this subject. Granularity is namespace-level in v0.1; per-tool
        toggles are tracked as <code className="rounded bg-muted px-1 py-0.5 text-xs">#2333</code>.
      </p>

      <PlatformSection testId={testId} tools={platform} />

      <ConnectorsSection
        testId={testId}
        groups={connectorGroups}
        parentUnitId={parentUnitId}
      />

      <ImageSection testId={testId} label={renderedImageLabel} tools={image} />
    </div>
  );
}

// ---------------------------------------------------------------------------
// Platform tier — collapsed <details>, expand for the full sv.* list.
// ---------------------------------------------------------------------------

function PlatformSection({
  testId,
  tools,
}: {
  testId: string;
  tools: readonly EffectiveToolResponse[];
}) {
  return (
    <section aria-label="Platform tools" data-testid={`${testId}-platform`}>
      <details className="group rounded-md border border-border">
        <summary className="flex cursor-pointer items-center gap-2 px-3 py-2 text-sm font-medium">
          <Sparkles className="h-4 w-4 text-muted-foreground" aria-hidden="true" />
          <span>Platform tools (sv.*) — always enabled</span>
          <span className="ml-auto text-xs text-muted-foreground">
            {tools.length} {tools.length === 1 ? "tool" : "tools"}
          </span>
        </summary>
        {tools.length === 0 ? (
          <p
            className="border-t border-border px-3 py-3 text-xs text-muted-foreground"
            data-testid={`${testId}-platform-empty`}
          >
            No platform tools registered.
          </p>
        ) : (
          <ul
            className="divide-y divide-border border-t border-border"
            data-testid={`${testId}-platform-list`}
          >
            {tools.map((tool) => (
              <li
                key={tool.name}
                className="flex flex-col gap-0.5 px-3 py-2 text-sm"
                data-testid={`${testId}-platform-tool-${tool.name}`}
              >
                <code className="font-mono text-xs">{tool.name}</code>
                {tool.description ? (
                  <span className="text-xs text-muted-foreground">
                    {tool.description}
                  </span>
                ) : null}
              </li>
            ))}
          </ul>
        )}
      </details>
    </section>
  );
}

// ---------------------------------------------------------------------------
// Connectors tier — one section per bound `connector:<slug>` group.
// ---------------------------------------------------------------------------

interface ConnectorGroup {
  slug: string;
  tools: readonly EffectiveToolResponse[];
  /** Non-null when every tool in the group carries the same inheritedFromUnitName. */
  inheritedFrom: string | null;
  /** True when entries are a mix of inherited + direct. */
  mixed: boolean;
}

function ConnectorsSection({
  testId,
  groups,
  parentUnitId,
}: {
  testId: string;
  groups: readonly ConnectorGroup[];
  parentUnitId: string | null;
}) {
  return (
    <section
      aria-label="Connector tools"
      className="space-y-2"
      data-testid={`${testId}-connectors`}
    >
      <header className="flex items-center gap-2 text-sm font-medium">
        <Wrench className="h-4 w-4 text-muted-foreground" aria-hidden="true" />
        <span>Connector tools</span>
      </header>

      {groups.length === 0 ? (
        <p
          className="rounded-md border border-dashed border-border bg-muted/10 px-3 py-3 text-xs text-muted-foreground"
          data-testid={`${testId}-connectors-empty`}
        >
          No connector tools granted. Bind a connector on the Connector
          sub-tab to grant a namespace.
        </p>
      ) : (
        <div className="space-y-3">
          {groups.map((group) => (
            <ConnectorGroupCard
              key={group.slug}
              testId={testId}
              group={group}
              parentUnitId={parentUnitId}
            />
          ))}
        </div>
      )}
    </section>
  );
}

function ConnectorGroupCard({
  testId,
  group,
  parentUnitId,
}: {
  testId: string;
  group: ConnectorGroup;
  parentUnitId: string | null;
}) {
  const isInherited = group.inheritedFrom !== null && !group.mixed;
  const cardClass = isInherited ? "opacity-60" : "";

  const inheritedLink =
    isInherited && parentUnitId
      ? `?node=${parentUnitId}&tab=Config&subtab=Tools`
      : null;

  return (
    <div
      className={`rounded-md border border-border ${cardClass}`}
      data-testid={`${testId}-connector-${group.slug}`}
      data-inherited={isInherited ? "true" : "false"}
    >
      <header className="flex items-center gap-2 px-3 py-2 text-sm font-medium">
        <code className="font-mono text-xs">{group.slug}</code>
        {isInherited ? (
          <Badge variant="outline" data-testid={`${testId}-connector-${group.slug}-inherited`}>
            {inheritedLink ? (
              <a href={inheritedLink} className="inline-flex items-center gap-1 underline">
                <Link2 className="h-3 w-3" aria-hidden="true" />
                Inherited from {group.inheritedFrom}
              </a>
            ) : (
              <span className="inline-flex items-center gap-1">
                <Link2 className="h-3 w-3" aria-hidden="true" />
                Inherited from {group.inheritedFrom}
              </span>
            )}
          </Badge>
        ) : (
          <Badge data-testid={`${testId}-connector-${group.slug}-enabled`} title={mixedTooltip(group)}>
            Enabled
          </Badge>
        )}
        <span className="ml-auto text-xs text-muted-foreground">
          {group.tools.length} {group.tools.length === 1 ? "tool" : "tools"}
        </span>
      </header>
      <ul
        className="divide-y divide-border border-t border-border"
        data-testid={`${testId}-connector-${group.slug}-list`}
      >
        {group.tools.map((tool) => (
          <li
            key={tool.name}
            className="flex flex-col gap-0.5 px-3 py-2 text-sm"
            data-testid={`${testId}-connector-${group.slug}-tool-${tool.name}`}
          >
            <code className="font-mono text-xs">{tool.name}</code>
            {tool.description ? (
              <span className="text-xs text-muted-foreground">
                {tool.description}
              </span>
            ) : null}
          </li>
        ))}
      </ul>
    </div>
  );
}

function mixedTooltip(group: ConnectorGroup): string | undefined {
  if (!group.mixed) {
    return undefined;
  }
  return (
    "Some tools in this namespace are inherited from a parent unit; " +
    "others are bound directly on this subject."
  );
}

// ---------------------------------------------------------------------------
// Image tier — read-only list of image-introspected tools.
// ---------------------------------------------------------------------------

function ImageSection({
  testId,
  label,
  tools,
}: {
  testId: string;
  label: string;
  tools: readonly EffectiveToolResponse[];
}) {
  return (
    <section
      aria-label="Image tools"
      className="space-y-2"
      data-testid={`${testId}-image`}
    >
      <header className="flex items-center gap-2 text-sm font-medium">
        <Wrench className="h-4 w-4 text-muted-foreground" aria-hidden="true" />
        <span>Image: <code className="font-mono text-xs">{label}</code></span>
        <span className="ml-auto text-xs text-muted-foreground">
          {tools.length} {tools.length === 1 ? "tool" : "tools"}
        </span>
      </header>
      {tools.length === 0 ? (
        <p
          className="rounded-md border border-dashed border-border bg-muted/10 px-3 py-3 text-xs text-muted-foreground"
          data-testid={`${testId}-image-empty`}
        >
          This image declares no custom tools.
        </p>
      ) : (
        <ul
          className="divide-y divide-border rounded-md border border-border"
          data-testid={`${testId}-image-list`}
        >
          {tools.map((tool) => (
            <li
              key={tool.name}
              className="flex flex-col gap-0.5 px-3 py-2 text-sm"
              data-testid={`${testId}-image-tool-${tool.name}`}
            >
              <code className="font-mono text-xs">{tool.name}</code>
              {tool.description ? (
                <span className="text-xs text-muted-foreground">
                  {tool.description}
                </span>
              ) : null}
            </li>
          ))}
        </ul>
      )}
    </section>
  );
}

// ---------------------------------------------------------------------------
// Helpers
// ---------------------------------------------------------------------------

function groupByConnector(
  tools: readonly EffectiveToolResponse[],
): readonly ConnectorGroup[] {
  const groupsBySlug = new Map<string, EffectiveToolResponse[]>();
  for (const tool of tools) {
    if (!tool.provenance.startsWith("connector:")) {
      continue;
    }
    const slug = tool.provenance.slice("connector:".length);
    const list = groupsBySlug.get(slug);
    if (list) {
      list.push(tool);
    } else {
      groupsBySlug.set(slug, [tool]);
    }
  }

  const result: ConnectorGroup[] = [];
  for (const [slug, groupTools] of groupsBySlug) {
    const inheritedSet = new Set(
      groupTools.map((t) => t.inheritedFromUnitName ?? "__direct__"),
    );
    const mixed = inheritedSet.size > 1;
    // The group "is inherited" when every entry carries the same
    // non-null inherited-from name. A mix → render the Enabled badge
    // (with a tooltip explaining the mixed case).
    const inheritedFrom = mixed
      ? null
      : (groupTools[0]?.inheritedFromUnitName ?? null);
    result.push({ slug, tools: groupTools, inheritedFrom, mixed });
  }
  // Sort by slug for a stable section order.
  result.sort((a, b) => a.slug.localeCompare(b.slug));
  return result;
}

