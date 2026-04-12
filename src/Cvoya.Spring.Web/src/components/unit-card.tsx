"use client";

import { Card, CardContent, CardHeader, CardTitle } from "@/components/ui/card";
import type { UnitDashboardSummary } from "@/lib/api/types";
import { timeAgo } from "@/lib/utils";
import Link from "next/link";

export function UnitCard({ unit }: { unit: UnitDashboardSummary }) {
  return (
    <Link href={`/units/${encodeURIComponent(unit.name)}`}>
      <Card className="transition-colors hover:bg-accent/50">
        <CardHeader className="pb-2">
          <CardTitle>{unit.displayName}</CardTitle>
        </CardHeader>
        <CardContent>
          <p className="text-xs text-muted-foreground">
            Registered {timeAgo(unit.registeredAt)}
          </p>
        </CardContent>
      </Card>
    </Link>
  );
}
