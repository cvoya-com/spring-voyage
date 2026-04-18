"use client";

// Analytics surface shell — renders the cross-tab nav so Costs /
// Throughput / Wait times feel like peers. Each tab is its own
// App-Router route (`/analytics/costs`, `/analytics/throughput`,
// `/analytics/waits`) so deep links are honest.
//
// Nav restructure (#444 / `docs/design/portal-exploration.md` § 5.7).

import Link from "next/link";
import { usePathname } from "next/navigation";
import type { ReactNode } from "react";
import { BarChart3, Clock, Wallet } from "lucide-react";

import { cn } from "@/lib/utils";

interface AnalyticsTab {
  href: string;
  label: string;
  icon: typeof Wallet;
}

const ANALYTICS_TABS: readonly AnalyticsTab[] = [
  { href: "/analytics/costs", label: "Costs", icon: Wallet },
  { href: "/analytics/throughput", label: "Throughput", icon: BarChart3 },
  { href: "/analytics/waits", label: "Wait times", icon: Clock },
];

export default function AnalyticsLayout({
  children,
}: {
  children: ReactNode;
}) {
  const pathname = usePathname();

  return (
    <div className="space-y-6">
      <nav
        aria-label="Analytics sections"
        className="flex flex-wrap items-center gap-1 rounded-lg border border-border bg-muted p-1"
      >
        {ANALYTICS_TABS.map((tab) => {
          const active = pathname === tab.href;
          const Icon = tab.icon;
          return (
            <Link
              key={tab.href}
              href={tab.href}
              aria-current={active ? "page" : undefined}
              className={cn(
                "inline-flex items-center gap-1.5 rounded-md px-3 py-1 text-sm font-medium transition-colors",
                active
                  ? "bg-background text-foreground shadow-sm"
                  : "text-muted-foreground hover:text-foreground",
              )}
            >
              <Icon className="h-3.5 w-3.5" />
              {tab.label}
            </Link>
          );
        })}
      </nav>
      {children}
    </div>
  );
}
