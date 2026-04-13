"use client";

import { useCallback, useEffect, useMemo, useRef, useState } from "react";
import Link from "next/link";
import {
  ArrowLeft,
  DollarSign,
  Github,
  KeyRound,
  Play,
  Settings,
  Square,
} from "lucide-react";

import { AgentsTab } from "./agents-tab";
import { SkillsTab } from "./skills-tab";

import { Badge } from "@/components/ui/badge";
import { Button } from "@/components/ui/button";
import {
  Card,
  CardContent,
  CardHeader,
  CardTitle,
} from "@/components/ui/card";
import { Input } from "@/components/ui/input";
import { Skeleton } from "@/components/ui/skeleton";
import { Tabs, TabsContent, TabsList, TabsTrigger } from "@/components/ui/tabs";
import { useToast } from "@/components/ui/toast";
import { api } from "@/lib/api/client";
import type {
  CostSummaryResponse,
  UnitResponse,
  UnitStatus,
} from "@/lib/api/types";
import { cn, formatCost } from "@/lib/utils";

// Follow-up issues referenced by this page: #124 unit-scoped agent
// assignment, #125 GitHub connector config, #122 unit secrets CRUD,
// #126 per-agent skill assignment.

function statusBadgeVariant(
  status: UnitStatus,
): "default" | "success" | "warning" | "destructive" | "outline" {
  switch (status) {
    case "Running":
      return "success";
    case "Starting":
    case "Stopping":
      return "warning";
    case "Error":
      return "destructive";
    case "Stopped":
      return "outline";
    case "Draft":
    default:
      return "default";
  }
}

interface ClientProps {
  id: string;
}

export default function UnitConfigClient({ id }: ClientProps) {
  const { toast } = useToast();

  const [unit, setUnit] = useState<UnitResponse | null>(null);
  const [status, setStatus] = useState<UnitStatus>("Draft");
  const [loading, setLoading] = useState(true);
  const [loadError, setLoadError] = useState<string | null>(null);

  // Edit form state (seeded from unit once loaded).
  const [formDisplayName, setFormDisplayName] = useState("");
  const [formDescription, setFormDescription] = useState("");
  const [formModel, setFormModel] = useState("");
  const [formColor, setFormColor] = useState("");
  const [saveError, setSaveError] = useState<string | null>(null);
  const [saving, setSaving] = useState(false);

  // Lifecycle action state.
  const [actionError, setActionError] = useState<string | null>(null);
  const [actionPending, setActionPending] = useState(false);

  const pollingRef = useRef<ReturnType<typeof setInterval> | null>(null);

  const [cost, setCost] = useState<CostSummaryResponse | null>(null);

  useEffect(() => {
    let cancelled = false;
    api
      .getUnitCost(id)
      .then((c) => {
        if (!cancelled) setCost(c);
      })
      .catch(() => {
        // Costs may legitimately be empty before any activity — swallow.
      });
    return () => {
      cancelled = true;
    };
  }, [id]);

  const applyUnit = useCallback((u: UnitResponse) => {
    setUnit(u);
    setStatus(u.status ?? "Draft");
    setFormDisplayName(u.displayName ?? "");
    setFormDescription(u.description ?? "");
    setFormModel(u.model ?? "");
    setFormColor(u.color ?? "");
  }, []);

  const refresh = useCallback(async () => {
    try {
      const u = await api.getUnit(id);
      applyUnit(u);
      setLoadError(null);
      return u;
    } catch (err) {
      const message = err instanceof Error ? err.message : String(err);
      setLoadError(message);
      return null;
    }
  }, [id, applyUnit]);

  useEffect(() => {
    let cancelled = false;
    setLoading(true);
    refresh().finally(() => {
      if (!cancelled) setLoading(false);
    });
    return () => {
      cancelled = true;
    };
  }, [refresh]);

  // Poll while transitional.
  useEffect(() => {
    const transitional = status === "Starting" || status === "Stopping";
    if (transitional) {
      if (pollingRef.current) return;
      pollingRef.current = setInterval(() => {
        refresh();
      }, 2000);
    } else if (pollingRef.current) {
      clearInterval(pollingRef.current);
      pollingRef.current = null;
    }
    return () => {
      if (pollingRef.current) {
        clearInterval(pollingRef.current);
        pollingRef.current = null;
      }
    };
  }, [status, refresh]);

  const startDisabled =
    actionPending || status === "Running" || status === "Starting";
  const stopDisabled =
    actionPending ||
    status === "Stopped" ||
    status === "Starting" ||
    status === "Draft";

  const handleStart = async () => {
    setActionError(null);
    setActionPending(true);
    try {
      const res = await api.startUnit(id);
      setStatus(res.status);
      toast({ title: "Unit started", description: id });
      refresh();
    } catch (err) {
      const message = err instanceof Error ? err.message : String(err);
      setActionError(message);
      toast({
        title: "Start failed",
        description: message,
        variant: "destructive",
      });
    } finally {
      setActionPending(false);
    }
  };

  const handleStop = async () => {
    setActionError(null);
    setActionPending(true);
    try {
      const res = await api.stopUnit(id);
      setStatus(res.status);
      toast({ title: "Unit stopped", description: id });
      refresh();
    } catch (err) {
      const message = err instanceof Error ? err.message : String(err);
      setActionError(message);
      toast({
        title: "Stop failed",
        description: message,
        variant: "destructive",
      });
    } finally {
      setActionPending(false);
    }
  };

  const handleSave = async () => {
    setSaveError(null);
    setSaving(true);
    try {
      const patch: Parameters<typeof api.updateUnit>[1] = {
        displayName: formDisplayName,
        description: formDescription,
        model: formModel,
        color: formColor,
      };
      const updated = await api.updateUnit(id, patch);
      applyUnit(updated);
      toast({ title: "Saved" });
    } catch (err) {
      const message = err instanceof Error ? err.message : String(err);
      setSaveError(message);
      toast({
        title: "Save failed",
        description: message,
        variant: "destructive",
      });
    } finally {
      setSaving(false);
    }
  };

  const colorSwatch = useMemo(() => {
    const c = (unit?.color ?? formColor) || "#6366f1";
    return c;
  }, [unit, formColor]);

  if (loading) {
    return (
      <div className="space-y-4">
        <Skeleton className="h-8 w-48" />
        <Skeleton className="h-40" />
        <Skeleton className="h-40" />
      </div>
    );
  }

  if (!unit) {
    return (
      <div className="space-y-4">
        <Link
          href="/units"
          className="flex items-center gap-1 text-sm text-muted-foreground hover:text-foreground"
        >
          <ArrowLeft className="h-4 w-4" /> Units
        </Link>
        <p className="text-muted-foreground">Unit not found.</p>
        {loadError && (
          <p className="rounded-md border border-destructive/50 bg-destructive/10 px-3 py-2 text-sm text-destructive">
            {loadError}
          </p>
        )}
      </div>
    );
  }

  return (
    <div className="space-y-6">
      <Link
        href="/units"
        className="inline-flex items-center gap-1 text-sm text-muted-foreground hover:text-foreground"
      >
        <ArrowLeft className="h-4 w-4" /> Units
      </Link>

      <div className="flex items-center gap-3">
        <span
          aria-label="Unit color"
          className="inline-block h-6 w-6 rounded-full border border-border"
          style={{ backgroundColor: colorSwatch }}
        />
        <div>
          <h1 className="text-2xl font-bold">{unit.displayName || unit.name}</h1>
          <p className="text-sm text-muted-foreground">{unit.name}</p>
        </div>
        <div className="ml-auto flex items-center gap-2">
          <Badge variant={statusBadgeVariant(status)}>{status}</Badge>
          <Button
            size="sm"
            onClick={handleStart}
            disabled={startDisabled}
          >
            <Play className="h-4 w-4 mr-1" /> Start
          </Button>
          <Button
            size="sm"
            variant="outline"
            onClick={handleStop}
            disabled={stopDisabled}
          >
            <Square className="h-4 w-4 mr-1" /> Stop
          </Button>
        </div>
      </div>

      {actionError && (
        <p className="rounded-md border border-destructive/50 bg-destructive/10 px-3 py-2 text-sm text-destructive">
          {actionError}
        </p>
      )}

      <Tabs defaultValue="general">
        <TabsList>
          <TabsTrigger value="general">General</TabsTrigger>
          <TabsTrigger value="agents">Agents</TabsTrigger>
          <TabsTrigger value="costs">Costs</TabsTrigger>
          <TabsTrigger value="connector">Connector</TabsTrigger>
          <TabsTrigger value="secrets">Secrets</TabsTrigger>
          <TabsTrigger value="skills">Skills</TabsTrigger>
        </TabsList>

        <TabsContent value="general" className="space-y-4">
          <Card>
            <CardHeader>
              <CardTitle>General</CardTitle>
            </CardHeader>
            <CardContent className="space-y-4">
              <label className="block space-y-1">
                <span className="text-sm text-muted-foreground">
                  Display name
                </span>
                <Input
                  value={formDisplayName}
                  onChange={(e) => setFormDisplayName(e.target.value)}
                />
              </label>

              <label className="block space-y-1">
                <span className="text-sm text-muted-foreground">
                  Description
                </span>
                <Input
                  value={formDescription}
                  onChange={(e) => setFormDescription(e.target.value)}
                />
              </label>

              <div className="grid grid-cols-1 gap-4 sm:grid-cols-2">
                <label className="block space-y-1">
                  <span className="text-sm text-muted-foreground">Model</span>
                  <Input
                    value={formModel}
                    onChange={(e) => setFormModel(e.target.value)}
                  />
                </label>
                <label className="block space-y-1">
                  <span className="text-sm text-muted-foreground">Color</span>
                  <div className="flex items-center gap-2">
                    <input
                      type="color"
                      value={formColor || "#6366f1"}
                      onChange={(e) => setFormColor(e.target.value)}
                      className="h-9 w-12 cursor-pointer rounded border border-input bg-background p-1"
                      aria-label="Pick color"
                    />
                    <Input
                      value={formColor}
                      onChange={(e) => setFormColor(e.target.value)}
                    />
                  </div>
                </label>
              </div>

              {saveError && (
                <p className="rounded-md border border-destructive/50 bg-destructive/10 px-3 py-2 text-sm text-destructive">
                  {saveError}
                </p>
              )}

              <div className="flex justify-end">
                <Button onClick={handleSave} disabled={saving}>
                  {saving ? "Saving…" : "Save"}
                </Button>
              </div>
            </CardContent>
          </Card>
        </TabsContent>

        <TabsContent value="agents">
          <AgentsTab unitId={id} />
        </TabsContent>

        <TabsContent value="costs">
          <Card>
            <CardHeader>
              <CardTitle className="flex items-center gap-2">
                <DollarSign className="h-4 w-4" /> Cost Breakdown
              </CardTitle>
            </CardHeader>
            <CardContent className="space-y-2 text-sm">
              {cost === null ? (
                <p className="text-muted-foreground">
                  No cost data available yet.
                </p>
              ) : (
                <>
                  <div className="flex justify-between">
                    <span className="text-muted-foreground">Total Cost</span>
                    <span className="font-medium">
                      {formatCost(cost.totalCost)}
                    </span>
                  </div>
                  <div className="flex justify-between">
                    <span className="text-muted-foreground">Input Tokens</span>
                    <span>{cost.totalInputTokens.toLocaleString()}</span>
                  </div>
                  <div className="flex justify-between">
                    <span className="text-muted-foreground">
                      Output Tokens
                    </span>
                    <span>{cost.totalOutputTokens.toLocaleString()}</span>
                  </div>
                  <div className="flex justify-between">
                    <span className="text-muted-foreground">Records</span>
                    <span>{cost.recordCount}</span>
                  </div>
                  <div className="flex justify-between text-xs text-muted-foreground">
                    <span>Period</span>
                    <span>
                      {new Date(cost.from).toLocaleDateString()} –{" "}
                      {new Date(cost.to).toLocaleDateString()}
                    </span>
                  </div>
                </>
              )}
            </CardContent>
          </Card>
        </TabsContent>

        <TabsContent value="connector">
          <PlaceholderCard
            icon={<Github className="h-5 w-5" />}
            title="Connector"
            body="GitHub connector configuration lives here. Follow-up: #125."
          />
        </TabsContent>

        <TabsContent value="secrets">
          <PlaceholderCard
            icon={<KeyRound className="h-5 w-5" />}
            title="Secrets"
            body="Unit secrets CRUD lives here. Follow-up: #122."
          />
        </TabsContent>

        <TabsContent value="skills">
          <SkillsTab unitId={id} />
        </TabsContent>
      </Tabs>
    </div>
  );
}

function PlaceholderCard({
  icon,
  title,
  body,
  footer,
}: {
  icon: React.ReactNode;
  title: string;
  body: string;
  footer?: React.ReactNode;
}) {
  return (
    <Card className={cn("bg-muted/40")}>
      <CardHeader>
        <CardTitle className="flex items-center gap-2 text-muted-foreground">
          {icon}
          <span>{title}</span>
          <Badge variant="outline" className="ml-2">
            <Settings className="mr-1 h-3 w-3" /> Not implemented
          </Badge>
        </CardTitle>
      </CardHeader>
      <CardContent className="space-y-2 text-sm text-muted-foreground">
        <p>{body}</p>
        {footer}
      </CardContent>
    </Card>
  );
}
