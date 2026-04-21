"use client";

// Tenant Activity tab (EXP-tab-tenant, umbrella #815 §4).
//
// Tenant-level activity is surfaced by the dedicated analytics routes
// (`/analytics/throughput` + `/analytics/waits`) — this tab is a jump
// point into them plus a tiny preview header so the surface isn't
// empty.

import Link from "next/link";
import { Activity, ArrowRight } from "lucide-react";

import {
  Card,
  CardContent,
  CardHeader,
  CardTitle,
} from "@/components/ui/card";

import { registerTab, type TabContentProps } from "./index";

function TenantActivityTab({ node }: TabContentProps) {
  if (node.kind !== "Tenant") return null;

  return (
    <Card data-testid="tab-tenant-activity">
      <CardHeader>
        <CardTitle className="flex items-center gap-2">
          <Activity className="h-4 w-4" aria-hidden="true" /> Tenant activity
        </CardTitle>
      </CardHeader>
      <CardContent className="space-y-3 text-sm">
        <p className="text-muted-foreground">
          Tenant-wide rollups live on the analytics surfaces.
        </p>
        <ul className="space-y-2">
          <li>
            <Link
              href="/analytics/throughput"
              className="inline-flex items-center gap-1 text-primary hover:underline"
              data-testid="tab-tenant-activity-throughput-link"
            >
              Throughput
              <ArrowRight className="h-3 w-3" aria-hidden="true" />
            </Link>
          </li>
          <li>
            <Link
              href="/analytics/waits"
              className="inline-flex items-center gap-1 text-primary hover:underline"
              data-testid="tab-tenant-activity-waits-link"
            >
              Wait times
              <ArrowRight className="h-3 w-3" aria-hidden="true" />
            </Link>
          </li>
        </ul>
      </CardContent>
    </Card>
  );
}

registerTab("Tenant", "Activity", TenantActivityTab);

export default TenantActivityTab;
