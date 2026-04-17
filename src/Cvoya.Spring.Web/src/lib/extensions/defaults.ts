// OSS defaults for the extension registry. This file is the only
// place the OSS build hard-codes the portal's routes and actions —
// everything downstream (sidebar, command palette) reads the merged
// registry and therefore never names a route directly.

import {
  Activity,
  LayoutDashboard,
  Network,
  Package,
  Plus,
  Play,
  Square,
  Wallet,
  Zap,
} from "lucide-react";

import type {
  IAuthContext,
  PaletteAction,
  RouteEntry,
} from "./types";

/**
 * OSS auth adapter. The OSS portal runs in daemon mode (no auth) so
 * the default reports a local user and grants every permission. The
 * hosted build replaces this adapter at `registerExtension` time with
 * an OAuth-backed implementation; call sites never change.
 */
export const defaultAuthContext: IAuthContext = {
  getUser: () => ({ id: "local", displayName: "local" }),
  hasPermission: () => true,
  getHeaders: () => ({}),
};

/**
 * Routes shipped with the OSS build. These match the current sidebar
 * today (#440 only introduces the seam; the nav restructure in #444
 * will evolve this list).
 */
export const defaultRoutes: readonly RouteEntry[] = [
  {
    path: "/",
    label: "Dashboard",
    icon: LayoutDashboard,
    navSection: "primary",
    orderHint: 10,
    keywords: ["home", "overview", "summary"],
    description: "Units, agents, and recent activity at a glance.",
  },
  {
    path: "/units",
    label: "Units",
    icon: Network,
    navSection: "primary",
    orderHint: 20,
    keywords: ["teams", "groups"],
    description: "Composite agents, policies, and connector bindings.",
  },
  {
    path: "/activity",
    label: "Activity",
    icon: Activity,
    navSection: "primary",
    orderHint: 30,
    keywords: ["events", "log", "stream", "audit"],
    description: "Raw activity event stream with filters.",
  },
  {
    path: "/initiative",
    label: "Initiative",
    icon: Zap,
    navSection: "primary",
    orderHint: 40,
    keywords: ["policy", "autonomy"],
    description: "Per-agent initiative policy editor.",
  },
  {
    path: "/budgets",
    label: "Budgets",
    icon: Wallet,
    navSection: "primary",
    orderHint: 50,
    keywords: ["cost", "spend", "limits"],
    description: "Tenant-wide and per-agent spend caps.",
  },
  {
    path: "/packages",
    label: "Packages",
    icon: Package,
    navSection: "primary",
    orderHint: 60,
    keywords: ["templates", "skills", "domain", "catalog"],
    description: "Browse installed packages and their unit/agent templates.",
  },
];

/**
 * Palette actions shipped with the OSS build. Each action maps to a
 * user-facing CLI verb family (`spring unit create`, `spring unit
 * start`, etc.) so the UI and CLI stay in parity. Many are simple
 * `href` navigations — the palette treats them identically to route
 * entries.
 */
export const defaultActions: readonly PaletteAction[] = [
  {
    id: "unit.create",
    label: "Create unit",
    icon: Plus,
    section: "actions",
    orderHint: 10,
    keywords: ["new", "unit", "team", "spring unit create"],
    description: "Open the new-unit wizard.",
    href: "/units/create",
  },
  {
    id: "unit.list",
    label: "List units",
    icon: Network,
    section: "actions",
    orderHint: 20,
    keywords: ["spring unit list"],
    href: "/units",
  },
  {
    id: "unit.start",
    label: "Start a unit",
    icon: Play,
    section: "actions",
    orderHint: 30,
    keywords: ["spring unit start", "run"],
    description: "Go to the units list to start a unit.",
    href: "/units",
  },
  {
    id: "unit.stop",
    label: "Stop a unit",
    icon: Square,
    section: "actions",
    orderHint: 40,
    keywords: ["spring unit stop", "halt"],
    description: "Go to the units list to stop a unit.",
    href: "/units",
  },
  {
    id: "activity.stream",
    label: "Stream activity",
    icon: Activity,
    section: "actions",
    orderHint: 50,
    keywords: ["spring activity stream", "tail", "logs"],
    href: "/activity",
  },
  {
    id: "budget.view",
    label: "View budgets",
    icon: Wallet,
    section: "actions",
    orderHint: 60,
    keywords: ["spring cost summary", "spring cost breakdown"],
    href: "/budgets",
  },
  {
    id: "initiative.view",
    label: "Edit initiative policy",
    icon: Zap,
    section: "actions",
    orderHint: 70,
    keywords: ["autonomy", "policy"],
    href: "/initiative",
  },
  {
    id: "packages.browse",
    label: "Browse packages",
    icon: Package,
    section: "actions",
    orderHint: 80,
    keywords: ["spring package list", "templates", "catalog"],
    description: "List installed packages and their templates.",
    href: "/packages",
  },
];
