"use client";

// Skills tab placeholder (#2354).
//
// The old EquippedSkillsTab body managed MCP-tool-style catalog entries
// which properly belong under Config → Tools (ToolsPanel). This file is
// now a pointer: it explains the Skills/Tools distinction and links the
// operator to the correct surface.
//
// The real authored-Skills feature (kind: Skill, package-shipped) is
// tracked separately; this placeholder occupies the tab position so the
// URL contract and tab registration don't change.

import { BookOpen, Sparkles } from "lucide-react";

export type EquippedSkillsSubjectKind = "Unit" | "Agent";

export interface EquippedSkillsTabProps {
  kind: EquippedSkillsSubjectKind;
  id: string;
  name: string;
}

export function EquippedSkillsTab({ kind, id }: EquippedSkillsTabProps) {
  const testId = kind === "Unit" ? "tab-unit-skills" : "tab-agent-skills";
  const toolsHref = `?node=${id}&tab=Config&subtab=Tools`;

  return (
    <div className="space-y-3" data-testid={testId}>
      <p className="text-xs text-muted-foreground">
        Skills are authored capabilities shipped in packages (
        <code className="rounded bg-muted px-1 py-0.5 text-xs">kind: Skill</code>
        ). They extend how an agent approaches tasks.
      </p>
      <div className="rounded-lg border border-dashed border-border bg-muted/30 p-6 text-center">
        <Sparkles
          className="mx-auto h-6 w-6 text-muted-foreground"
          aria-hidden="true"
        />
        <p className="mt-2 text-sm font-medium">Skills coming soon</p>
        <p className="mt-1 text-xs text-muted-foreground">
          The authored-Skills surface is in progress.
        </p>
        <p className="mt-3 text-xs text-muted-foreground">
          <BookOpen className="mr-1 inline-block h-3 w-3" aria-hidden="true" />
          For runtime invocation tools (platform{" "}
          <code className="rounded bg-muted px-1 py-0.5 text-xs">sv.*</code>,
          connector, and image tools), see{" "}
          <a
            href={toolsHref}
            className="underline underline-offset-2 hover:text-foreground"
          >
            Config → Tools
          </a>
          .
        </p>
      </div>
    </div>
  );
}
