"use client";

import { useMemo, useState, type ReactNode } from "react";
import { AlertTriangle, Check, Package, Search } from "lucide-react";

import { Button } from "@/components/ui/button";
import {
  Card,
  CardContent,
  CardHeader,
  CardTitle,
} from "@/components/ui/card";
import { Input } from "@/components/ui/input";
import { Skeleton } from "@/components/ui/skeleton";
import { usePackages } from "@/lib/api/queries";
import type { PackageSummary } from "@/lib/api/types";
import { cn } from "@/lib/utils";

export interface SourcePackagePickerProps {
  onSelect: (packageName: string) => void;
  onBack: () => void;
  onCancel: () => void;
  selectedPackageName?: string | null;
  onSelectionChange?: (packageName: string) => void;
  selectionDetail?: ReactNode;
}

export function SourcePackagePicker({
  onSelect,
  onBack,
  onCancel,
  selectedPackageName: controlledSelectedPackageName,
  onSelectionChange,
  selectionDetail,
}: SourcePackagePickerProps) {
  const packagesQuery = usePackages();
  const [search, setSearch] = useState("");
  const [
    internalSelectedPackageName,
    setInternalSelectedPackageName,
  ] = useState<string | null>(null);
  const selectedPackageName =
    controlledSelectedPackageName ?? internalSelectedPackageName;

  const filteredPackages = useMemo(() => {
    const query = search.trim().toLowerCase();
    return (packagesQuery.data ?? [])
      .filter((pkg) => packageAgentTemplateCount(pkg) > 0)
      .filter((pkg) => {
        if (query.length === 0) return true;
        return packageSearchText(pkg).includes(query);
      });
  }, [packagesQuery.data, search]);

  const selectedPackage = filteredPackages.find(
    (pkg) => pkg.name === selectedPackageName,
  );
  const selectPackage = (packageName: string) => {
    if (controlledSelectedPackageName === undefined) {
      setInternalSelectedPackageName(packageName);
    }
    onSelectionChange?.(packageName);
  };
  const errorMessage =
    packagesQuery.error instanceof Error
      ? packagesQuery.error.message
      : packagesQuery.error
        ? String(packagesQuery.error)
        : "Could not load package catalog.";

  return (
    <div className="space-y-4">
      <Card>
        <CardHeader>
          <CardTitle className="flex items-center gap-2">
            <Package className="h-5 w-5" aria-hidden />
            Package picker
          </CardTitle>
        </CardHeader>
        <CardContent className="space-y-4">
          <label className="block space-y-1">
            <span className="text-sm text-muted-foreground">
              Search packages
            </span>
            <div className="relative">
              <Search
                className="pointer-events-none absolute left-3 top-2.5 h-4 w-4 text-muted-foreground"
                aria-hidden
              />
              <Input
                type="search"
                value={search}
                onChange={(e) => setSearch(e.target.value)}
                placeholder="Search by name"
                aria-label="Search packages"
                data-testid="package-picker-search"
                className="pl-9"
                disabled={packagesQuery.isPending}
              />
            </div>
          </label>

          <div data-testid="package-picker-list" className="space-y-2">
            {packagesQuery.isPending ? (
              <PackagePickerSkeleton />
            ) : packagesQuery.isError ? (
              <div
                role="alert"
                className="flex items-start gap-2 rounded-md border border-destructive/50 bg-destructive/10 px-3 py-2 text-sm text-destructive"
              >
                <AlertTriangle
                  className="mt-0.5 h-4 w-4 shrink-0"
                  aria-hidden
                />
                <span>Failed to load catalog: {errorMessage}</span>
              </div>
            ) : filteredPackages.length === 0 ? (
              <p className="rounded-md border border-border bg-muted/30 px-3 py-6 text-center text-sm text-muted-foreground">
                No packages found.
              </p>
            ) : (
              <div
                role="radiogroup"
                aria-label="Agent package"
                className="max-h-80 space-y-2 overflow-y-auto pr-1"
              >
                {filteredPackages.map((pkg) => {
                  const isSelected = pkg.name === selectedPackageName;
                  return (
                    <button
                      key={pkg.name}
                      type="button"
                      role="radio"
                      aria-checked={isSelected}
                      data-testid={`package-picker-item-${pkg.name}`}
                      onClick={() => selectPackage(pkg.name)}
                      className={cn(
                        "flex w-full items-start gap-3 rounded-md border p-3 text-left text-sm transition-colors focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring focus-visible:ring-offset-2",
                        isSelected
                          ? "border-primary bg-primary/5 shadow-sm"
                          : "border-border hover:border-primary/40 hover:bg-accent/50",
                      )}
                    >
                      <span
                        className={cn(
                          "mt-0.5 flex h-4 w-4 shrink-0 items-center justify-center rounded-full border",
                          isSelected
                            ? "border-primary bg-primary text-primary-foreground"
                            : "border-border",
                        )}
                        aria-hidden
                      >
                        {isSelected && <Check className="h-3 w-3" />}
                      </span>
                      <span className="min-w-0 flex-1">
                        <span className="block truncate font-medium">
                          {packageDisplayName(pkg)}
                        </span>
                        {pkg.description && (
                          <span className="mt-1 block line-clamp-2 text-xs text-muted-foreground">
                            {pkg.description}
                          </span>
                        )}
                        <span className="mt-1 block text-[11px] text-muted-foreground">
                          {formatAgentTemplateCount(
                            packageAgentTemplateCount(pkg),
                          )}
                        </span>
                      </span>
                    </button>
                  );
                })}
              </div>
            )}
          </div>

          {selectionDetail}
        </CardContent>
      </Card>

      <div className="flex items-center justify-between gap-2">
        <Button type="button" variant="outline" onClick={onBack}>
          Back
        </Button>
        <div className="flex items-center gap-2">
          <Button type="button" variant="outline" onClick={onCancel}>
            Cancel
          </Button>
          <Button
            type="button"
            data-testid="package-picker-confirm"
            disabled={!selectedPackage}
            onClick={() => {
              if (selectedPackage) onSelect(selectedPackage.name);
            }}
          >
            Confirm
          </Button>
        </div>
      </div>
    </div>
  );
}

function PackagePickerSkeleton() {
  return (
    <div className="space-y-2" aria-label="Loading packages">
      {[0, 1, 2].map((row) => (
        <div
          key={row}
          className="flex items-start gap-3 rounded-md border border-border p-3"
        >
          <Skeleton className="mt-0.5 h-4 w-4 rounded-full" />
          <div className="flex-1 space-y-2">
            <Skeleton className="h-4 w-1/3" />
            <Skeleton className="h-3 w-3/4" />
            <Skeleton className="h-3 w-24" />
          </div>
        </div>
      ))}
    </div>
  );
}

function packageDisplayName(pkg: PackageSummary): string {
  const displayName =
    "displayName" in pkg && typeof pkg.displayName === "string"
      ? pkg.displayName.trim()
      : "";
  return displayName || pkg.name;
}

function packageSearchText(pkg: PackageSummary): string {
  return `${packageDisplayName(pkg)} ${pkg.name}`.toLowerCase();
}

function packageAgentTemplateCount(pkg: PackageSummary): number {
  return normaliseCount(pkg.agentTemplateCount);
}

function normaliseCount(value: number | string | null | undefined): number {
  const parsed =
    typeof value === "number"
      ? value
      : typeof value === "string"
        ? Number.parseInt(value, 10)
        : 0;
  return Number.isFinite(parsed) ? parsed : 0;
}

function formatAgentTemplateCount(count: number): string {
  return `${count} agent template${count === 1 ? "" : "s"}`;
}
