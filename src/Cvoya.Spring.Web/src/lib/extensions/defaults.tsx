// OSS defaults for the extension registry. This file is the only
// place the OSS build hard-codes the portal's routes and actions —
// everything downstream (sidebar, command palette) reads the merged
// registry and therefore never names a route directly.

import {
  Activity,
  GraduationCap,
  Inbox,
  Info,
  LayoutDashboard,
  MessagesSquare,
  Network,
  Package,
  Plug,
  Plus,
  Play,
  Square,
  UserCircle,
  Users,
  Wallet,
  Zap,
} from "lucide-react";

import { AboutPanel } from "@/components/settings/about-panel";
import { AuthPanel } from "@/components/settings/auth-panel";
import { BudgetPanel } from "@/components/settings/budget-panel";

import type {
  DrawerPanel,
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
    path: "/inbox",
    label: "Inbox",
    icon: Inbox,
    navSection: "primary",
    orderHint: 15,
    keywords: [
      "awaiting",
      "pending",
      "human",
      "spring inbox list",
    ],
    description:
      "Conversations awaiting a response from you.",
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
    path: "/agents",
    label: "Agents",
    icon: Users,
    navSection: "primary",
    orderHint: 22,
    keywords: [
      "agent",
      "roster",
      "directory",
      "spring agent list",
    ],
    description:
      "Every agent across every unit, filter by status, unit, or expertise.",
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
    path: "/conversations",
    label: "Conversations",
    icon: MessagesSquare,
    navSection: "primary",
    orderHint: 35,
    keywords: ["chat", "thread", "message", "spring conversation list"],
    description: "Message threads between humans, agents, and units.",
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
    path: "/connectors",
    label: "Connectors",
    icon: Plug,
    navSection: "primary",
    orderHint: 55,
    keywords: ["integrations", "github", "webhook", "spring connector catalog"],
    description: "Catalog of connector types and which units bind them.",
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
  {
    path: "/directory",
    label: "Directory",
    icon: GraduationCap,
    navSection: "primary",
    orderHint: 65,
    keywords: [
      "expertise",
      "domains",
      "search",
      "capabilities",
      "spring agent expertise",
      "spring unit expertise",
    ],
    description:
      "Browse and search expertise declared by every agent and unit.",
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
    id: "agent.list",
    label: "List agents",
    icon: Users,
    section: "actions",
    orderHint: 22,
    keywords: ["spring agent list", "roster", "directory"],
    description: "Browse every agent across every unit.",
    href: "/agents",
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
    id: "conversation.list",
    label: "List conversations",
    icon: MessagesSquare,
    section: "actions",
    orderHint: 55,
    keywords: ["spring conversation list", "threads", "chat"],
    description: "Browse message threads between humans, agents, and units.",
    href: "/conversations",
  },
  {
    id: "inbox.list",
    label: "Open inbox",
    icon: Inbox,
    section: "actions",
    orderHint: 56,
    keywords: [
      "spring inbox list",
      "awaiting",
      "pending",
      "human",
    ],
    description: "Conversations awaiting a response from you.",
    href: "/inbox",
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
  {
    id: "connectors.catalog",
    label: "Browse connectors",
    icon: Plug,
    section: "actions",
    orderHint: 90,
    keywords: ["spring connector catalog", "integrations"],
    description: "List every connector type the server knows about.",
    href: "/connectors",
  },
  {
    id: "directory.expertise",
    label: "Browse expertise",
    icon: GraduationCap,
    section: "actions",
    orderHint: 100,
    keywords: [
      "spring agent expertise",
      "spring unit expertise",
      "domains",
      "capabilities",
      "search",
    ],
    description:
      "Search the tenant's expertise directory across every agent and unit.",
    href: "/directory",
  },
];

/**
 * Settings-drawer panels shipped with the OSS build (#451 — PR-S1
 * Sub-PR D). Every interactive control inside a default panel has a
 * matching CLI verb (`spring cost set-budget`, `spring platform info`,
 * `spring auth token list`); the hosted build plugs in additional
 * panels (tenant secrets, members / RBAC, SSO) by returning them from
 * `registerExtension({ drawerPanels: [...] })` — no OSS fork required.
 *
 * Panel ordering is driven by `orderHint` alone; callers never assume
 * a fixed panel count. Extension authors pick their own `orderHint`
 * values (hosted panels tend to use `orderHint >= 100` to sit after
 * the OSS defaults).
 */
export const defaultDrawerPanels: readonly DrawerPanel[] = [
  {
    id: "budget",
    label: "Tenant budget",
    icon: Wallet,
    description:
      "Daily cost ceiling across every agent and unit in this tenant.",
    orderHint: 10,
    component: <BudgetPanel />,
  },
  {
    id: "auth",
    label: "Account",
    icon: UserCircle,
    description: "Current session and API tokens.",
    orderHint: 20,
    component: <AuthPanel />,
  },
  {
    id: "about",
    label: "About",
    icon: Info,
    description: "Platform version and license reference.",
    orderHint: 90,
    component: <AboutPanel />,
  },
];
