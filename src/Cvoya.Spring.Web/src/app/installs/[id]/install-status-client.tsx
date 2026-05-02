"use client";

/**
 * InstallStatusClient — polling install-status view for /installs/[id].
 *
 * State machine:
 *   unknown id → 404 card
 *   loading    → skeleton
 *   staging    → spinner + per-package staging detail; polls every 2 s
 *   active     → success card + per-package summary; stops polling
 *   failed     → error card + per-package error detail + Retry + Abort
 *
 * ADR-0035 decision 11: Phase-2 failures leave staging rows visible.
 * This view is the operator's recovery surface.
 */

import Link from "next/link";
import { useRouter } from "next/navigation";
import {
  CheckCircle,
  Loader2,
  XCircle,
  RefreshCw,
  Ban,
  Package as PackageIcon,
} from "lucide-react";

import { Breadcrumbs } from "@/components/breadcrumbs";
import { Badge } from "@/components/ui/badge";
import { Button } from "@/components/ui/button";
import { Card, CardContent, CardHeader, CardTitle } from "@/components/ui/card";
import { Skeleton } from "@/components/ui/skeleton";
import { useToast } from "@/components/ui/toast";
import {
  useInstallStatus,
  useRetryInstall,
  useAbortInstall,
} from "@/lib/api/queries";
import type { InstallPackageDetail } from "@/lib/api/types";

interface Props {
  id: string;
}

/** 2-second polling interval while the install is in-progress. */
const POLLING_INTERVAL_MS = 2000;

export default function InstallStatusClient({ id }: Props) {
  const router = useRouter();
  const { toast } = useToast();

  // Derive the polling interval from the last-known status so polling stops
  // automatically once we reach a terminal state (active or failed).
  const statusQuery = useInstallStatus(id, {
    refetchInterval: (query) => {
      const data = query.state.data;
      if (!data || data.status === "active" || data.status === "failed") {
        return false;
      }
      return POLLING_INTERVAL_MS;
    },
  });

  const retryMutation = useRetryInstall(id);
  const abortMutation = useAbortInstall(id);

  async function handleRetry() {
    try {
      await retryMutation.mutateAsync();
    } catch (err) {
      toast({
        title: "Retry failed",
        description: err instanceof Error ? err.message : "Unknown error",
        variant: "destructive",
      });
    }
  }

  async function handleAbort() {
    const pkg = statusQuery.data?.packages?.[0];
    const packageName = pkg?.packageName ?? "the package";
    try {
      await abortMutation.mutateAsync();
      toast({ title: `Install aborted for ${packageName}.` });
      // Redirect back to the package detail page if we know the name,
      // otherwise fall back to the catalog index.
      const href =
        pkg?.packageName
          ? `/settings/packages/${encodeURIComponent(pkg.packageName)}`
          : "/settings/packages";
      router.push(href);
    } catch (err) {
      toast({
        title: "Abort failed",
        description: err instanceof Error ? err.message : "Unknown error",
        variant: "destructive",
      });
    }
  }

  // Loading
  if (statusQuery.isPending) {
    return (
      <div className="space-y-4" data-testid="install-status-loading">
        <Skeleton className="h-8 w-48" />
        <Skeleton className="h-32" />
        <Skeleton className="h-24" />
      </div>
    );
  }

  // Not found
  if (statusQuery.data === null || statusQuery.error) {
    return (
      <div className="space-y-4">
        <Breadcrumbs
          items={[
            { label: "Packages", href: "/settings/packages" },
            { label: "Install status" },
          ]}
        />
        <Card>
          <CardContent className="p-6">
            <p className="text-sm text-destructive" role="alert" data-testid="install-not-found">
              {statusQuery.error
                ? `Failed to load install status: ${statusQuery.error.message}`
                : `Install "${id}" not found. It may have been aborted or the id is invalid.`}
            </p>
          </CardContent>
        </Card>
      </div>
    );
  }

  const status = statusQuery.data;
  const aggregateStatus = status.status; // "staging" | "active" | "failed"

  return (
    <div className="space-y-6" data-testid={`install-status-${aggregateStatus}`}>
      <Breadcrumbs
        items={[
          { label: "Packages", href: "/settings/packages" },
          { label: "Install status" },
        ]}
      />

      {/* Aggregate status header */}
      <div className="flex items-start justify-between gap-4">
        <div>
          <h1 className="flex items-center gap-2 text-2xl font-bold">
            <StatusIcon status={aggregateStatus} />
            {aggregateStatus === "staging" && "Installing…"}
            {aggregateStatus === "active" && "Install complete"}
            {aggregateStatus === "failed" && "Install failed"}
          </h1>
          <p className="mt-1 text-sm text-muted-foreground">
            Install ID:{" "}
            <code className="rounded bg-muted px-1 py-0.5 text-xs">{id}</code>
          </p>
          {status.startedAt && (
            <p className="mt-0.5 text-xs text-muted-foreground">
              Started:{" "}
              <time dateTime={status.startedAt}>
                {new Date(status.startedAt).toLocaleString()}
              </time>
            </p>
          )}
        </div>

        {/* Recovery actions — shown only in the failed state */}
        {aggregateStatus === "failed" && (
          <div className="flex shrink-0 gap-2">
            <Button
              variant="outline"
              onClick={handleRetry}
              disabled={retryMutation.isPending || abortMutation.isPending}
              data-testid="retry-button"
            >
              <RefreshCw className="mr-2 h-4 w-4" aria-hidden="true" />
              {retryMutation.isPending ? "Retrying…" : "Retry"}
            </Button>
            <Button
              variant="destructive"
              onClick={handleAbort}
              disabled={retryMutation.isPending || abortMutation.isPending}
              data-testid="abort-button"
            >
              <Ban className="mr-2 h-4 w-4" aria-hidden="true" />
              {abortMutation.isPending ? "Aborting…" : "Abort"}
            </Button>
          </div>
        )}
      </div>

      {/* Top-level error (Phase-1 failures — rare since those produce 4xx) */}
      {status.error && (
        <Card className="border-destructive/50">
          <CardContent className="p-4">
            <p className="text-sm text-destructive" role="alert">
              {status.error}
            </p>
          </CardContent>
        </Card>
      )}

      {/* Per-package detail */}
      <Card>
        <CardHeader>
          <CardTitle className="flex items-center gap-2 text-base">
            <PackageIcon className="h-4 w-4" aria-hidden="true" />
            Package details ({status.packages.length})
          </CardTitle>
        </CardHeader>
        <CardContent>
          <div className="space-y-3" role="list" aria-label="Per-package install status">
            {status.packages.map((pkg) => (
              <PackageDetailRow
                key={pkg.packageName}
                detail={pkg}
                overallStatus={aggregateStatus}
              />
            ))}
          </div>
        </CardContent>
      </Card>

      {/* Active: show links to the newly-installed packages */}
      {aggregateStatus === "active" && (
        <Card className="border-green-500/30 bg-green-500/5 dark:border-green-500/20 dark:bg-green-500/5">
          <CardContent className="p-4">
            <p className="text-sm font-medium text-green-700 dark:text-green-400">
              All packages installed successfully.
            </p>
            <div className="mt-2 flex flex-wrap gap-2">
              {status.packages.map((pkg) => (
                <Link
                  key={pkg.packageName}
                  href={`/settings/packages/${encodeURIComponent(pkg.packageName)}`}
                  className="text-xs text-primary hover:underline"
                >
                  View {pkg.packageName}
                </Link>
              ))}
            </div>
          </CardContent>
        </Card>
      )}
    </div>
  );
}

// ---------------------------------------------------------------------------
// Sub-components
// ---------------------------------------------------------------------------

function StatusIcon({ status }: { status: string }) {
  if (status === "active") {
    return (
      <CheckCircle
        className="h-6 w-6 text-green-600 dark:text-green-400"
        aria-hidden="true"
      />
    );
  }
  if (status === "failed") {
    return (
      <XCircle
        className="h-6 w-6 text-destructive"
        aria-hidden="true"
      />
    );
  }
  // staging
  return (
    <Loader2
      className="h-6 w-6 animate-spin text-muted-foreground"
      aria-hidden="true"
      aria-label="Installing"
    />
  );
}

function PackageDetailRow({
  detail,
  overallStatus,
}: {
  detail: InstallPackageDetail;
  overallStatus: string;
}) {
  const stateVariant =
    detail.state === "active"
      ? "success"
      : detail.state === "failed"
        ? "destructive"
        : "secondary";

  return (
    <div
      role="listitem"
      className="rounded border border-border p-3"
      data-testid={`package-detail-row-${detail.packageName}`}
    >
      <div className="flex items-center justify-between gap-2">
        <p className="text-sm font-medium">{detail.packageName}</p>
        <div className="flex items-center gap-2">
          {detail.state === "staging" && overallStatus === "staging" && (
            <Loader2
              className="h-3.5 w-3.5 animate-spin text-muted-foreground"
              aria-hidden="true"
            />
          )}
          <Badge variant={stateVariant} data-testid={`package-state-${detail.packageName}`}>
            {detail.state}
          </Badge>
        </div>
      </div>
      {detail.state === "failed" && detail.errorMessage && (
        <p className="mt-2 text-xs text-destructive" role="alert">
          {detail.errorMessage}
        </p>
      )}
    </div>
  );
}
