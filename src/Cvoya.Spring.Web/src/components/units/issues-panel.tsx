"use client";

import Link from "next/link";

import { AlertTriangle, ChevronRight, Info } from "lucide-react";

import type {
  IssueChildSummaryResponse,
  IssueResponse,
  IssuesViewResponse,
} from "@/lib/api/types";

/**
 * #2160: unified operational-issues surface used on the unit Overview
 * tab and the unit's Agents-tab agent overview. Renders the subject's
 * own open issues followed by a per-immediate-child rollup that links
 * into the child's own Overview surface for drill-down.
 *
 * Severity buckets are rendered in two visually distinct rows so the
 * operator can scan errors at a glance without parsing per-issue copy.
 * The empty state ("No open issues.") is rendered explicitly so the
 * Overview tab can still mount the panel and show the operator
 * affirmatively that all is well, instead of an absent affordance that
 * leaves them wondering whether the surface is wired at all.
 */
export interface IssuesPanelProps {
  view: IssuesViewResponse | null;
  /**
   * Subject kind for the parent (the panel's mounting context). Drives
   * the empty-state copy and the per-child link target. Agents have
   * no rollup, so when subjectKind === "agent" the child list is never
   * shown.
   */
  subjectKind: "unit" | "agent";
}

export function IssuesPanel({ view, subjectKind }: IssuesPanelProps) {
  const own = view?.own ?? [];
  const rollup = view?.descendants ?? null;

  const hasOwn = own.length > 0;
  const hasDescendantIssues =
    subjectKind === "unit"
      ? Boolean(rollup && (rollup.errorCount + rollup.warningCount > 0))
      : false;

  if (!hasOwn && !hasDescendantIssues) {
    return (
      <div
        role="status"
        data-testid="issues-panel"
        data-has-issues="false"
        className="flex items-start gap-2 rounded-md border border-emerald-500/40 bg-emerald-500/10 px-3 py-2 text-sm text-emerald-900 dark:text-emerald-200"
      >
        <Info className="mt-0.5 h-4 w-4 shrink-0" aria-hidden />
        <span>
          {subjectKind === "unit"
            ? "No open issues on this unit or any of its members."
            : "No open issues on this agent."}
        </span>
      </div>
    );
  }

  return (
    <div data-testid="issues-panel" data-has-issues="true" className="space-y-3">
      {hasOwn && (
        <ul className="space-y-2">
          {own.map((issue) => (
            <IssueRow key={issue.id} issue={issue} />
          ))}
        </ul>
      )}

      {hasDescendantIssues && rollup && (
        <DescendantRollup rollup={rollup} />
      )}
    </div>
  );
}

function IssueRow({ issue }: { issue: IssueResponse }) {
  const severityClass =
    issue.severity === "error"
      ? "border-destructive/50 bg-destructive/10 text-destructive"
      : "border-warning/50 bg-warning/15 text-foreground";
  const iconClass =
    issue.severity === "error"
      ? "text-destructive"
      : "text-warning";
  return (
    <li
      role="alert"
      data-testid="issues-panel-row"
      data-severity={issue.severity}
      data-source={issue.source}
      data-code={issue.code}
      className={`flex items-start gap-2 rounded-md border px-3 py-2 text-sm ${severityClass}`}
    >
      <AlertTriangle className={`mt-0.5 h-4 w-4 shrink-0 ${iconClass}`} aria-hidden />
      <div className="min-w-0 flex-1">
        <p className="font-medium">{issue.title}</p>
        {issue.detail && (
          <p className="mt-1 text-foreground/90">{issue.detail}</p>
        )}
        <p className="mt-1 text-[11px] uppercase tracking-wide text-muted-foreground">
          {issue.source} · {issue.code}
        </p>
      </div>
    </li>
  );
}

function DescendantRollup({
  rollup,
}: {
  rollup: NonNullable<IssuesViewResponse["descendants"]>;
}) {
  return (
    <div
      data-testid="issues-panel-descendants"
      data-error-count={rollup.errorCount}
      data-warning-count={rollup.warningCount}
      className="rounded-md border border-border bg-muted/30 px-3 py-2 text-sm"
    >
      <p className="font-medium">
        Members with open issues:{" "}
        <span className="text-destructive">{rollup.errorCount} errors</span>
        {" · "}
        <span className="text-warning">{rollup.warningCount} warnings</span>
      </p>
      <ul className="mt-2 space-y-1">
        {rollup.byChild.map((child) => (
          <ChildLink key={`${child.subjectKind}:${child.subjectId}`} child={child} />
        ))}
      </ul>
    </div>
  );
}

function ChildLink({ child }: { child: IssueChildSummaryResponse }) {
  // The unit Overview lives at `/units/{name}` (or wherever the
  // explorer routes); we don't have a route table here, so we link
  // by id and let the existing routing surface decide how to render.
  const href =
    child.subjectKind === "unit"
      ? `/units/${child.subjectId}`
      : `/units?agent=${child.subjectId}`;
  return (
    <li>
      <Link
        href={href}
        data-testid="issues-panel-child-link"
        data-subject-kind={child.subjectKind}
        data-subject-id={child.subjectId}
        className="flex items-center justify-between gap-2 rounded px-2 py-1 hover:bg-accent"
      >
        <span className="truncate">
          {child.name || "(unnamed)"}{" "}
          <span className="text-xs text-muted-foreground">
            ({child.subjectKind})
          </span>
        </span>
        <span className="flex items-center gap-2 text-xs">
          {child.errorCount > 0 && (
            <span className="rounded bg-destructive/15 px-1.5 py-0.5 font-medium text-destructive">
              {child.errorCount}E
            </span>
          )}
          {child.warningCount > 0 && (
            <span className="rounded bg-warning/15 px-1.5 py-0.5 font-medium text-foreground">
              {child.warningCount}W
            </span>
          )}
          <ChevronRight className="h-3 w-3" aria-hidden />
        </span>
      </Link>
    </li>
  );
}
