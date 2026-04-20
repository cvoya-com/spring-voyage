"use client";

/**
 * Shared primitives for the read-only admin surfaces (#691).
 *
 * The admin section is the portal-facing half of the AGENTS.md "admin
 * surfaces are CLI-only" carve-out: install/configure/credential
 * validation ride `spring`, the portal renders visibility-only tables
 * and a consistent credential-health badge. These primitives keep the
 * two admin routes (`/admin/agent-runtimes`, `/admin/connectors`)
 * visually consistent without each page reinventing the patterns.
 */

import Link from "next/link";
import { Terminal } from "lucide-react";

import { Badge } from "@/components/ui/badge";
import { Card, CardContent } from "@/components/ui/card";
import type { CredentialHealthStatus } from "@/lib/api/types";

interface CliCalloutProps {
  /** CLI command family the operator should reach for (e.g. `spring agent-runtime`). */
  cliCommand: string;
  /** Path to the user-guide doc under `docs/user-guide/`. */
  docsHref?: string;
  /** Visible label for the docs link, when shown. */
  docsLabel?: string;
}

/**
 * Inline banner that reminds the operator this surface is read-only.
 * Used at the top of every `/admin/*` page so the CLI-only carve-out is
 * visible without reading the operator docs first.
 */
export function CliCallout({ cliCommand, docsHref, docsLabel }: CliCalloutProps) {
  return (
    <Card
      role="note"
      aria-label="Managed via CLI"
      className="border-primary/30 bg-primary/5"
    >
      <CardContent className="flex flex-col gap-2 p-4 text-sm sm:flex-row sm:items-start sm:justify-between">
        <div className="flex items-start gap-2">
          <Terminal
            className="mt-0.5 h-4 w-4 flex-none text-primary"
            aria-hidden="true"
          />
          <div>
            <p className="font-medium text-foreground">
              Read-only view — mutations go through the CLI.
            </p>
            <p className="mt-1 text-muted-foreground">
              Install, uninstall, configure, and credential validation ride{" "}
              <code className="rounded bg-muted px-1 py-0.5 text-xs">
                {cliCommand}
              </code>
              .
            </p>
          </div>
        </div>
        {docsHref && (
          <Link
            href={docsHref}
            className="shrink-0 rounded-md px-3 py-1.5 text-xs font-medium text-primary hover:underline focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring"
          >
            {docsLabel ?? "Operator guide"}
          </Link>
        )}
      </CardContent>
    </Card>
  );
}

const STATUS_VARIANT: Record<
  CredentialHealthStatus,
  "success" | "warning" | "destructive" | "outline"
> = {
  Valid: "success",
  Unknown: "outline",
  Invalid: "destructive",
  Expired: "destructive",
  Revoked: "destructive",
};

const STATUS_LABEL: Record<CredentialHealthStatus, string> = {
  Unknown: "Unknown",
  Valid: "Valid",
  Invalid: "Invalid",
  Expired: "Expired",
  Revoked: "Revoked",
};

interface CredentialHealthBadgeProps {
  /**
   * The credential-health status, or `null` when the endpoint returned
   * 404 (row not recorded yet). A null value renders a muted "No signal
   * yet" badge — operators should run the matching validate-credential
   * CLI verb to seed the row.
   */
  status: CredentialHealthStatus | null;
  /** Test hook so the admin page tests can disambiguate sibling badges. */
  "data-testid"?: string;
}

export function CredentialHealthBadge({
  status,
  "data-testid": testId,
}: CredentialHealthBadgeProps) {
  if (status === null) {
    return (
      <Badge variant="outline" data-testid={testId}>
        No signal yet
      </Badge>
    );
  }
  return (
    <Badge variant={STATUS_VARIANT[status]} data-testid={testId}>
      {STATUS_LABEL[status]}
    </Badge>
  );
}

/**
 * Render an ISO timestamp as a locale-formatted string inside a `<time>`
 * element. Falls back to the raw value for safety when `Date.parse`
 * fails so the cell never ends up blank.
 */
export function Timestamp({ value }: { value: string | null | undefined }) {
  if (!value) {
    return <span className="text-muted-foreground">—</span>;
  }
  const parsed = Date.parse(value);
  if (Number.isNaN(parsed)) {
    return <span className="text-muted-foreground">{value}</span>;
  }
  return (
    <time dateTime={value} className="text-muted-foreground">
      {new Date(parsed).toLocaleString()}
    </time>
  );
}
