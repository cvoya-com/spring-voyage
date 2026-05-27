"use client";

// Activity surface shell — segmented tab nav between Events (raw event
// stream, the original /activity content) and Interactions (graph /
// matrix / timeline view of who is talking to whom). Each tab is its
// own App-Router route so deep links are honest:
//   - /activity/events       → src/app/activity/events/page.tsx
//   - /activity/interactions → src/app/activity/interactions/page.tsx
//
// Mirrors the segmented-tab shell pattern used by `/analytics` (#2867).

import Link from "next/link";
import { usePathname } from "next/navigation";
import type { ReactNode } from "react";
import { Activity, Network } from "lucide-react";

import { cn } from "@/lib/utils";

interface ActivityTab {
  href: string;
  label: string;
  icon: typeof Activity;
}

const ACTIVITY_TABS: readonly ActivityTab[] = [
  { href: "/activity/events", label: "Events", icon: Activity },
  { href: "/activity/interactions", label: "Interactions", icon: Network },
];

export default function ActivityLayout({
  children,
}: {
  children: ReactNode;
}) {
  const pathname = usePathname();

  return (
    <div className="space-y-6">
      <nav
        aria-label="Activity sections"
        data-testid="activity-tabs"
        className="flex flex-wrap items-center gap-1 rounded-full border border-border bg-muted/60 p-1"
      >
        {ACTIVITY_TABS.map((tab) => {
          const active =
            pathname === tab.href || pathname.startsWith(tab.href + "/");
          const Icon = tab.icon;
          return (
            <Link
              key={tab.href}
              href={tab.href}
              aria-current={active ? "page" : undefined}
              data-testid={`activity-tab-${tab.href.split("/").pop()}`}
              className={cn(
                "inline-flex items-center gap-1.5 rounded-full px-3 py-1 text-sm font-medium transition-colors",
                active
                  ? "bg-primary/15 text-primary shadow-sm"
                  : "text-muted-foreground hover:text-foreground",
              )}
            >
              <Icon className="h-3.5 w-3.5" aria-hidden="true" />
              {tab.label}
            </Link>
          );
        })}
      </nav>
      {children}
    </div>
  );
}
