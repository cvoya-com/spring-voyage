"use client";

// Unified Traces tab (canonical-tabs.md § 5.7, #2272).
//
// In v0.1 traces are a deterministic mock fixture — the real backend
// endpoint ships in `V21-traces-api`. The Explorer surface must still
// render something so reviewers can verify layout + the forthcoming
// empty-state copy. Downstream issues swap this in for the real hook
// once it lands; the same fixture is reused on both subjects so the
// Unit and Agent surfaces light up the moment the backend ships.
//
// Per canonical-tabs.md § 4 row 7 / § 5.7 and
// `docs/concepts/units-vs-agents.md` rule 3 — a unit is an agent and
// the Traces surface applies identically to both subjects.

import { Zap } from "lucide-react";

import { Badge } from "@/components/ui/badge";
import {
  Table,
  TableBody,
  TableCell,
  TableHead,
  TableHeader,
  TableRow,
} from "@/components/ui/table";

export type TracesSubjectKind = "Unit" | "Agent";

export interface TracesTabProps {
  /** Subject kind — drives the data-testid roots. */
  kind: TracesSubjectKind;
  /** Stable id of the subject (unit id or agent id). Seeds the fixture. */
  id: string;
}

interface MockTrace {
  id: string;
  startedAt: string;
  durationMs: number;
  cost: number;
}

function buildMockTraces(id: string): MockTrace[] {
  // Deterministic fixture — same id always yields the same rows so
  // snapshot-style eyeballs don't drift between loads.
  const seed = id.length + 3;
  return Array.from({ length: 6 }, (_, i) => ({
    id: `trace-${id.slice(0, 6)}-${(seed * (i + 1)).toString(16)}`,
    startedAt: new Date(
      Date.now() - (i + 1) * 37 * 60 * 1000,
    ).toISOString(),
    durationMs: 600 + ((seed * (i + 2)) % 3200),
    cost: Number((((seed * (i + 1)) % 47) / 1000).toFixed(4)),
  }));
}

export function TracesTab({ kind, id }: TracesTabProps) {
  const rows = buildMockTraces(id);
  const testIdRoot = `tab-${kind.toLowerCase()}-traces`;

  return (
    <div className="space-y-3" data-testid={testIdRoot}>
      <div
        role="note"
        className="flex items-start gap-2 rounded-md border border-border bg-muted/30 p-3 text-xs text-muted-foreground"
        data-testid={`${testIdRoot}-mock-note`}
      >
        <Zap className="mt-0.5 h-4 w-4 shrink-0" aria-hidden="true" />
        <span>
          These rows are a static fixture. Real traces land in{" "}
          <Badge variant="outline" className="mx-1">
            V21-traces-api
          </Badge>
          — the Explorer wiring is in place so the surface lights up the
          moment the backend ships.
        </span>
      </div>
      <Table>
        <TableHeader>
          <TableRow>
            <TableHead>Trace ID</TableHead>
            <TableHead>Started</TableHead>
            <TableHead className="text-right">Duration</TableHead>
            <TableHead className="text-right">Cost</TableHead>
          </TableRow>
        </TableHeader>
        <TableBody>
          {rows.map((row) => (
            <TableRow key={row.id}>
              <TableCell className="font-mono text-xs">{row.id}</TableCell>
              <TableCell className="whitespace-nowrap text-xs text-muted-foreground">
                {row.startedAt}
              </TableCell>
              <TableCell className="text-right text-xs">
                {row.durationMs.toLocaleString()} ms
              </TableCell>
              <TableCell className="text-right font-mono text-xs">
                ${row.cost.toFixed(4)}
              </TableCell>
            </TableRow>
          ))}
        </TableBody>
      </Table>
    </div>
  );
}
