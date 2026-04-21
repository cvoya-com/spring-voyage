"use client";

import { useEffect, useState } from "react";
import {
  AlertTriangle,
  CheckCircle2,
  ChevronDown,
  ChevronRight,
  CircleSlash,
  RefreshCw,
  ShieldAlert,
  ShieldCheck,
} from "lucide-react";

import { Badge } from "@/components/ui/badge";
import { Button } from "@/components/ui/button";
import { Card, CardContent, CardHeader, CardTitle } from "@/components/ui/card";

// Wire shape that mirrors Cvoya.Spring.Core.Configuration.ConfigurationReport.
// Hand-written here because the OpenAPI regen for `/api/v1/system/configuration`
// may not have landed yet; the shape is stable (see #616).
type ConfigurationStatus = "Met" | "Disabled" | "Invalid";
type SeverityLevel = "Information" | "Warning" | "Error";
type ReportStatus = "Healthy" | "Degraded" | "Failed";

interface RequirementStatusDto {
  requirementId: string;
  displayName: string;
  description: string;
  isMandatory: boolean;
  status: ConfigurationStatus;
  severity: SeverityLevel;
  reason?: string | null;
  suggestion?: string | null;
  environmentVariableNames: readonly string[];
  configurationSectionPath?: string | null;
  documentationUrl?: string | null;
}

interface SubsystemDto {
  subsystemName: string;
  status: ReportStatus;
  requirements: readonly RequirementStatusDto[];
}

interface ConfigurationReportDto {
  status: ReportStatus;
  generatedAt: string;
  subsystems: readonly SubsystemDto[];
}

const BASE = process.env.NEXT_PUBLIC_API_URL ?? "";

async function fetchReport(): Promise<ConfigurationReportDto> {
  const resp = await fetch(`${BASE}/api/v1/system/configuration`, {
    cache: "no-store",
  });
  if (!resp.ok) {
    throw new Error(
      `GET /api/v1/system/configuration failed: ${resp.status} ${resp.statusText}`,
    );
  }
  return (await resp.json()) as ConfigurationReportDto;
}

function reportVariant(
  status: ReportStatus,
): "success" | "warning" | "destructive" {
  switch (status) {
    case "Healthy":
      return "success";
    case "Degraded":
      return "warning";
    case "Failed":
    default:
      return "destructive";
  }
}

function requirementVariant(
  status: ConfigurationStatus,
  severity: SeverityLevel,
): "success" | "warning" | "destructive" {
  if (status === "Invalid" || severity === "Error") return "destructive";
  if (status === "Disabled" || severity === "Warning") return "warning";
  return "success";
}

function RequirementIcon({
  status,
  severity,
}: {
  status: ConfigurationStatus;
  severity: SeverityLevel;
}) {
  if (status === "Invalid" || severity === "Error") {
    return <ShieldAlert className="h-4 w-4 text-destructive" aria-hidden />;
  }
  if (status === "Disabled") {
    return <CircleSlash className="h-4 w-4 text-warning" aria-hidden />;
  }
  if (severity === "Warning") {
    return <AlertTriangle className="h-4 w-4 text-warning" aria-hidden />;
  }
  return <CheckCircle2 className="h-4 w-4 text-success" aria-hidden />;
}

function SubsystemCard({ subsystem }: { subsystem: SubsystemDto }) {
  const [open, setOpen] = useState(
    subsystem.status !== "Healthy" /* show non-healthy by default */,
  );

  return (
    <Card>
      <button
        type="button"
        onClick={() => setOpen((v) => !v)}
        className="flex w-full items-center gap-2 px-4 py-3 text-left hover:bg-accent/50 rounded-t-md transition-colors"
        aria-expanded={open}
      >
        {open ? (
          <ChevronDown className="h-4 w-4 shrink-0 text-muted-foreground" aria-hidden />
        ) : (
          <ChevronRight className="h-4 w-4 shrink-0 text-muted-foreground" aria-hidden />
        )}
        <CardTitle className="flex-1 text-base font-semibold">
          {subsystem.subsystemName}
        </CardTitle>
        <Badge variant={reportVariant(subsystem.status)}>{subsystem.status}</Badge>
      </button>
      {open && (
        <CardContent className="pt-0 space-y-3">
          {subsystem.requirements.map((req) => (
            <div
              key={req.requirementId}
              className="rounded-md border border-border bg-muted/30 p-3 space-y-2"
              data-testid={`requirement-${req.requirementId}`}
            >
              <div className="flex flex-wrap items-center gap-2">
                <RequirementIcon status={req.status} severity={req.severity} />
                <div className="font-medium">{req.displayName}</div>
                <Badge variant={requirementVariant(req.status, req.severity)}>
                  {req.status}
                </Badge>
                {req.severity !== "Information" && (
                  <Badge variant="outline">{req.severity}</Badge>
                )}
                <Badge variant="outline">
                  {req.isMandatory ? "mandatory" : "optional"}
                </Badge>
              </div>
              <p className="text-sm text-muted-foreground">{req.description}</p>
              {req.reason && (
                <p className="text-sm">
                  <span className="font-medium">Reason:</span> {req.reason}
                </p>
              )}
              {req.suggestion && (
                <p className="text-sm">
                  <span className="font-medium">Suggestion:</span> {req.suggestion}
                </p>
              )}
              {(req.environmentVariableNames.length > 0 ||
                req.configurationSectionPath) && (
                <div className="text-xs text-muted-foreground space-x-2">
                  {req.environmentVariableNames.length > 0 && (
                    <span>
                      env:{" "}
                      <code className="rounded bg-background px-1 py-0.5">
                        {req.environmentVariableNames.join(", ")}
                      </code>
                    </span>
                  )}
                  {req.configurationSectionPath && (
                    <span>
                      section:{" "}
                      <code className="rounded bg-background px-1 py-0.5">
                        {req.configurationSectionPath}
                      </code>
                    </span>
                  )}
                </div>
              )}
              {req.documentationUrl && (
                <a
                  href={req.documentationUrl}
                  target="_blank"
                  rel="noreferrer"
                  className="text-xs text-primary hover:underline"
                >
                  Documentation
                </a>
              )}
            </div>
          ))}
        </CardContent>
      )}
    </Card>
  );
}

export default function SystemConfigurationPage() {
  const [report, setReport] = useState<ConfigurationReportDto | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [loading, setLoading] = useState(false);

  async function refresh() {
    setLoading(true);
    setError(null);
    try {
      const data = await fetchReport();
      setReport(data);
    } catch (err) {
      setError(err instanceof Error ? err.message : String(err));
    } finally {
      setLoading(false);
    }
  }

  useEffect(() => {
    void refresh();
  }, []);

  return (
    <div className="space-y-4">
      <div className="flex flex-col gap-3 sm:flex-row sm:items-center sm:justify-between">
        <h1 className="text-2xl font-bold flex items-center gap-2">
          <ShieldCheck className="h-6 w-6" aria-hidden />
          System configuration
        </h1>
        <Button
          variant="outline"
          size="sm"
          onClick={() => void refresh()}
          disabled={loading}
          className="self-start sm:self-auto"
          aria-label="Refresh configuration report"
        >
          <RefreshCw
            className={`h-4 w-4 mr-1 ${loading ? "animate-spin" : ""}`}
            aria-hidden
          />
          Refresh
        </Button>
      </div>

      <Card>
        <CardHeader>
          <CardTitle className="text-base flex items-center gap-2">
            What is this?
          </CardTitle>
        </CardHeader>
        <CardContent className="text-sm text-muted-foreground">
          The platform validates tier-1 configuration (environment variables,{" "}
          <code>appsettings.json</code>, mounted secrets) at startup. The report
          below shows, per subsystem, whether each requirement is satisfied,
          degraded, disabled, or misconfigured. See{" "}
          <code>docs/architecture/configuration.md</code> for the framework
          contract and <code>spring system configuration</code> for the
          equivalent CLI verb.
        </CardContent>
      </Card>

      {error && (
        <div
          role="alert"
          className="rounded-md border border-destructive/50 bg-destructive/10 px-3 py-2 text-sm text-destructive"
        >
          {error}
        </div>
      )}

      {report && (
        <Card>
          <CardHeader>
            <CardTitle className="text-base flex items-center gap-2">
              <Badge variant={reportVariant(report.status)}>{report.status}</Badge>
              <span>Overall platform configuration</span>
            </CardTitle>
          </CardHeader>
          <CardContent className="text-sm text-muted-foreground">
            Snapshot taken at{" "}
            <time dateTime={report.generatedAt}>
              {new Date(report.generatedAt).toLocaleString()}
            </time>{" "}
            — the report is cached at startup and does not refresh until the
            host restarts.
          </CardContent>
        </Card>
      )}

      {report?.subsystems.map((subsystem) => (
        <SubsystemCard key={subsystem.subsystemName} subsystem={subsystem} />
      ))}

      {report && report.subsystems.length === 0 && !loading && (
        <p className="text-sm text-muted-foreground">
          No subsystems have registered configuration requirements.
        </p>
      )}
    </div>
  );
}
