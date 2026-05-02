"use client";

/**
 * /packages — installed-package browser (#395 / PR-PLAT-PKG-1).
 *
 * Mirrors `spring package list` verbatim: same endpoint, same shape,
 * same sort order. Cards surface the per-content counts so a user can
 * pick "engineering-team is inside software-engineering" at a glance
 * without drilling into the detail page.
 *
 * Browse / Upload stub (ADR-0035 decision 5 / #1565): the file picker and
 * card are visible but the submit action is disabled for v0.1. The stub
 * keeps the surface honest so doc and UX align with the v0.2 final shape.
 * Operators who need to install a local package today use the CLI:
 *   spring package install --file ./my-package.yaml
 *
 * Design contract: docs/design/portal-exploration.md § 3.2 lists
 * Packages as a primary nav entry (sidebar link wired in
 * `lib/extensions/defaults.ts`), § 5.1 describes the card-grid layout
 * that matches the Stitch/DESIGN.md primitives used by AgentCard and
 * UnitCard.
 */

import Link from "next/link";
import { Package as PackageIcon, Upload } from "lucide-react";
import { useRef } from "react";

import { Badge } from "@/components/ui/badge";
import { Button } from "@/components/ui/button";
import { Card, CardContent, CardHeader, CardTitle } from "@/components/ui/card";
import { Skeleton } from "@/components/ui/skeleton";
import { Tooltip } from "@/components/ui/tooltip";
import { usePackages } from "@/lib/api/queries";
import type { PackageSummary } from "@/lib/api/types";
import { cn } from "@/lib/utils";

export default function PackagesListPage() {
  const packagesQuery = usePackages();
  const packages = packagesQuery.data ?? [];
  const loading = packagesQuery.isPending;
  const fileInputRef = useRef<HTMLInputElement>(null);

  return (
    <div className="space-y-6">
      <div>
        <h1 className="flex items-center gap-2 text-2xl font-bold">
          <PackageIcon className="h-5 w-5" /> Packages
        </h1>
        <p className="text-sm text-muted-foreground">
          Installed domain packages and the templates, skills, and
          connectors they contribute. Matches{" "}
          <code className="rounded bg-muted px-1 py-0.5 text-xs">
            spring package list
          </code>
          .
        </p>
      </div>

      {/* Browse / Upload stub — ADR-0035 decision 5 / 13. v0.1: disabled
          submit; v0.2 will accept a real upload. The file picker is visible
          so the UX shape matches the final design without requiring v0.2 work. */}
      <BrowseUploadStub fileInputRef={fileInputRef} />

      {loading ? (
        <div className="grid grid-cols-1 gap-3 sm:grid-cols-2 lg:grid-cols-3">
          <Skeleton className="h-40" />
          <Skeleton className="h-40" />
          <Skeleton className="h-40" />
        </div>
      ) : packagesQuery.error ? (
        <Card>
          <CardContent className="p-6">
            <p className="text-sm text-destructive" role="alert">
              Failed to load packages: {packagesQuery.error.message}
            </p>
          </CardContent>
        </Card>
      ) : packages.length === 0 ? (
        <Card>
          <CardContent className="space-y-1 p-6">
            <p className="text-sm text-muted-foreground">
              No packages installed. The server&apos;s{" "}
              <code className="rounded bg-muted px-1 py-0.5 text-xs">
                Packages:Root
              </code>{" "}
              setting controls where the catalog reads from; when it
              isn&apos;t pointed at a valid directory the list stays
              empty.
            </p>
          </CardContent>
        </Card>
      ) : (
        <div className="grid grid-cols-1 gap-3 sm:grid-cols-2 lg:grid-cols-3">
          {packages.map((p) => (
            <PackageCard key={p.name} pkg={p} />
          ))}
        </div>
      )}
    </div>
  );
}

/**
 * Browse / Upload package stub (ADR-0035 decision 5 / 13).
 *
 * v0.1: the file picker is visible and the operator can select a file, but
 * the Upload button is disabled with a tooltip explaining the CLI path.
 * v0.2 will wire the multipart POST to `/api/v1/packages/install/file`.
 */
function BrowseUploadStub({
  fileInputRef,
}: {
  fileInputRef: React.RefObject<HTMLInputElement | null>;
}) {
  return (
    <Card
      data-testid="browse-upload-stub"
      className="border-dashed border-border/70 bg-muted/20"
    >
      <CardHeader className="pb-2">
        <CardTitle className="flex items-center gap-2 text-sm font-medium">
          <Upload className="h-4 w-4" aria-hidden="true" />
          Browse / Upload package
          <Badge variant="outline" className="ml-1 text-xs">
            Coming in v0.2
          </Badge>
        </CardTitle>
      </CardHeader>
      <CardContent className="space-y-3">
        <p className="text-xs text-muted-foreground">
          Upload a local <code className="rounded bg-muted px-1 py-0.5">package.yaml</code> or
          a tarball to install a package directly from your machine. This
          feature arrives in v0.2.
        </p>

        {/* File picker — visible but submit is disabled */}
        <div className="flex items-center gap-2">
          <input
            ref={fileInputRef}
            type="file"
            accept=".yaml,.yml,.tar,.tgz"
            aria-label="Select package file"
            data-testid="browse-file-input"
            className="flex-1 rounded-md border border-input bg-background px-3 py-1.5 text-xs file:mr-2 file:rounded file:border-0 file:bg-secondary file:px-2 file:py-0.5 file:text-xs file:font-medium file:text-secondary-foreground"
          />
          <Tooltip label="Browse-and-upload arrives in v0.2. Use the CLI today." side="top">
            <Button
              type="button"
              size="sm"
              disabled
              aria-disabled="true"
              data-testid="browse-upload-button"
            >
              Upload
            </Button>
          </Tooltip>
        </div>

        {/* CLI hint for v0.1 */}
        <p className="text-xs text-muted-foreground" data-testid="browse-cli-hint">
          To install a local package today, run:{" "}
          <code className="rounded bg-muted px-1 py-0.5 text-xs font-mono">
            spring package install --file ./my-package.yaml
          </code>
          . See the{" "}
          <Link
            href="https://docs.spring.voyage/cli/package"
            className="text-primary underline-offset-2 hover:underline"
            target="_blank"
            rel="noopener noreferrer"
          >
            CLI reference
          </Link>{" "}
          for full options.
        </p>
      </CardContent>
    </Card>
  );
}

function PackageCard({ pkg }: { pkg: PackageSummary }) {
  const href = `/settings/packages/${encodeURIComponent(pkg.name ?? "")}`;
  const name = pkg.name ?? "(unnamed package)";
  return (
    <Card
      data-testid={`package-card-${pkg.name}`}
      className={cn(
        "h-full transition-colors hover:border-primary/50 hover:bg-muted/30",
      )}
    >
      <CardContent className="p-4">
        <Link
          href={href}
          aria-label={`Open package ${name}`}
          className="flex items-start justify-between gap-2"
        >
          <div className="min-w-0 flex-1">
            <h3 className="truncate font-semibold">{name}</h3>
            {pkg.description && (
              <p className="mt-1 line-clamp-2 text-xs text-muted-foreground">
                {pkg.description}
              </p>
            )}
          </div>
        </Link>

        <div className="mt-3 flex flex-wrap items-center gap-1.5 text-xs">
          <CountBadge label="units" value={pkg.unitTemplateCount} />
          <CountBadge label="agents" value={pkg.agentTemplateCount} />
          <CountBadge label="skills" value={pkg.skillCount} />
          <CountBadge label="connectors" value={pkg.connectorCount} />
          <CountBadge label="workflows" value={pkg.workflowCount} />
        </div>
      </CardContent>
    </Card>
  );
}

/**
 * Counts are int-shaped on the wire, but openapi-typescript surfaces
 * them as `number | string | null | undefined` because the generator
 * widens every `format: int32` field to accommodate extreme-precision
 * string serialisation (see openapi.json schema transformer in
 * Program.cs). Normalise to a number here so the badge still renders a
 * count even when the wire payload came back as the string variant;
 * treat missing values as `0` so a single malformed row doesn't hide
 * the whole card.
 */
function CountBadge({
  label,
  value,
}: {
  label: string;
  value: number | string | null | undefined;
}) {
  const parsed =
    typeof value === "number"
      ? value
      : typeof value === "string"
        ? Number.parseInt(value, 10)
        : 0;
  const count = Number.isFinite(parsed) ? parsed : 0;
  return (
    <Badge
      variant={count === 0 ? "outline" : "secondary"}
      data-testid={`package-count-${label}`}
    >
      {count} {label}
    </Badge>
  );
}
