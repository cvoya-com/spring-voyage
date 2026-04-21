"use client";

// Tenant Policies tab (EXP-tab-tenant, umbrella #815 §4).
//
// Tenant-scoped policies live on the dedicated `/policies` surface;
// this tab is a light cross-link so operators can reach the full
// policy matrix without leaving the Explorer.

import Link from "next/link";
import { ArrowRight, ShieldCheck } from "lucide-react";

import {
  Card,
  CardContent,
  CardHeader,
  CardTitle,
} from "@/components/ui/card";

import { registerTab, type TabContentProps } from "./index";

function TenantPoliciesTab({ node }: TabContentProps) {
  if (node.kind !== "Tenant") return null;

  return (
    <Card data-testid="tab-tenant-policies">
      <CardHeader>
        <CardTitle className="flex items-center gap-2">
          <ShieldCheck className="h-4 w-4" aria-hidden="true" /> Tenant policies
        </CardTitle>
      </CardHeader>
      <CardContent className="space-y-3 text-sm">
        <p className="text-muted-foreground">
          Tenant-wide guard rails — initiative levels, cost caps, skill
          allow-lists — live on the dedicated policies surface.
        </p>
        <Link
          href="/policies"
          className="inline-flex items-center gap-1 text-primary hover:underline"
          data-testid="tab-tenant-policies-link"
        >
          Open policies
          <ArrowRight className="h-3 w-3" aria-hidden="true" />
        </Link>
      </CardContent>
    </Card>
  );
}

registerTab("Tenant", "Policies", TenantPoliciesTab);

export default TenantPoliciesTab;
