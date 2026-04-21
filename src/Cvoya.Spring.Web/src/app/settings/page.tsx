"use client";

/**
 * /settings — Settings hub (#862 / SET-hub).
 *
 * The v2 IA (plan § 9 of umbrella #815) retires the in-shell Settings
 * drawer in favour of a dedicated page. The hub surfaces two things:
 *
 *  1. The four tenant-panel cards (Tenant budget, Tenant defaults,
 *     Account, About) promoted verbatim from the legacy
 *     `SettingsDrawer`. Each panel is rendered inline inside a
 *     `<Card>` with its `label` + `description` as header chrome.
 *     The merged drawer-panel registry is read via `useDrawerPanels()`
 *     so hosted extensions that register additional panels (e.g.
 *     Members / RBAC, SSO) get them surfaced in the hub too — the same
 *     extension seam the retired drawer used.
 *
 *  2. A tile grid of links into the Settings subpages
 *     (`/settings/skills`, `/settings/packages`, `/settings/agent-runtimes`,
 *     `/settings/system-configuration`). Those routes host the content
 *     that used to live at the retired top-level paths (`/skills`,
 *     `/packages`, `/admin/agent-runtimes`, `/system/configuration`).
 *     The legacy routes keep rendering until their `DEL-*-top`
 *     follow-ups land.
 */

import Link from "next/link";
import {
  Cpu,
  GraduationCap,
  Package as PackageIcon,
  Settings as SettingsIcon,
  ShieldCheck,
  type LucideIcon,
} from "lucide-react";

import { Card, CardContent, CardHeader, CardTitle } from "@/components/ui/card";
import { useDrawerPanels } from "@/lib/extensions/context";

interface SettingsTile {
  href: string;
  label: string;
  description: string;
  icon: LucideIcon;
}

/**
 * The four subpage tiles rendered below the tenant-panel grid. Order
 * matches plan § 9: catalog surfaces first (Skills, Packages), then
 * the admin-carveout surfaces (Agent runtimes, System configuration).
 */
const SETTINGS_TILES: readonly SettingsTile[] = [
  {
    href: "/settings/skills",
    label: "Skills",
    description: "Tenant skill catalog grouped by registry.",
    icon: GraduationCap,
  },
  {
    href: "/settings/packages",
    label: "Packages",
    description:
      "Installed domain packages and the templates, skills, and connectors they contribute.",
    icon: PackageIcon,
  },
  {
    href: "/settings/agent-runtimes",
    label: "Agent runtimes",
    description:
      "Installed agent runtimes on the tenant, their model catalogs, and credential health.",
    icon: Cpu,
  },
  {
    href: "/settings/system-configuration",
    label: "System configuration",
    description:
      "Startup configuration report (subsystems, environment variables, credentials).",
    icon: ShieldCheck,
  },
];

export default function SettingsPage() {
  const panels = useDrawerPanels();

  return (
    <div className="space-y-6">
      <div>
        <h1 className="flex items-center gap-2 text-2xl font-bold">
          <SettingsIcon className="h-5 w-5" aria-hidden="true" /> Settings
        </h1>
        <p className="text-sm text-muted-foreground">
          Tenant defaults, account, and the catalog & admin surfaces
          (Skills, Packages, Agent runtimes, System configuration).
        </p>
      </div>

      {panels.length > 0 && (
        <section aria-labelledby="settings-panels-heading" className="space-y-3">
          <h2
            id="settings-panels-heading"
            className="text-sm font-medium uppercase tracking-wide text-muted-foreground"
          >
            Tenant
          </h2>
          <div
            className="grid grid-cols-1 gap-3 md:grid-cols-2"
            data-testid="settings-panels-grid"
          >
            {panels.map((panel) => {
              const Icon = panel.icon;
              return (
                <Card
                  key={panel.id}
                  data-testid={`settings-panel-card-${panel.id}`}
                >
                  <CardHeader className="gap-1">
                    <CardTitle className="flex items-center gap-2 text-base">
                      <Icon
                        className="h-4 w-4 text-muted-foreground"
                        aria-hidden="true"
                      />
                      {panel.label}
                    </CardTitle>
                    {panel.description && (
                      <p className="text-xs text-muted-foreground">
                        {panel.description}
                      </p>
                    )}
                  </CardHeader>
                  <CardContent>{panel.component}</CardContent>
                </Card>
              );
            })}
          </div>
        </section>
      )}

      <section aria-labelledby="settings-tiles-heading" className="space-y-3">
        <h2
          id="settings-tiles-heading"
          className="text-sm font-medium uppercase tracking-wide text-muted-foreground"
        >
          Catalog & admin
        </h2>
        <div
          className="grid grid-cols-1 gap-3 sm:grid-cols-2"
          data-testid="settings-tiles-grid"
        >
          {SETTINGS_TILES.map((tile) => {
            const Icon = tile.icon;
            return (
              <Card
                key={tile.href}
                className="transition-colors hover:border-primary/50 hover:bg-muted/30"
                data-testid={`settings-tile-${tile.href.replace(/\//g, "-").replace(/^-/, "")}`}
              >
                <Link
                  href={tile.href}
                  className="block h-full rounded-lg p-4 focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring"
                >
                  <div className="flex items-start gap-3">
                    <span className="flex h-9 w-9 flex-none items-center justify-center rounded-md bg-muted text-muted-foreground">
                      <Icon className="h-4 w-4" aria-hidden="true" />
                    </span>
                    <div className="min-w-0 flex-1">
                      <p className="font-semibold">{tile.label}</p>
                      <p className="mt-1 text-xs text-muted-foreground">
                        {tile.description}
                      </p>
                    </div>
                  </div>
                </Link>
              </Card>
            );
          })}
        </div>
      </section>
    </div>
  );
}
