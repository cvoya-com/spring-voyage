// OSS defaults for the extension registry. This file is the only
// place the OSS build hard-codes the portal's routes and actions —
// everything downstream (sidebar, command palette) reads the merged
// registry and therefore never names a route directly.
//
// The v2 IA (plan §2 of umbrella #815) groups the sidebar into three
// visible clusters:
//
//   • Overview     — Dashboard, Activity, Analytics
//   • Orchestrate  — Units (Explorer), Inbox, Discovery
//   • Control      — Connectors, Policies, Budgets, Settings
//
// Every route below declares which cluster it belongs to via
// `navSection`; the sidebar reads `NAV_SECTION_ORDER` to decide the
// render order.

import {
  Activity,
  BarChart3,
  Compass,
  GraduationCap,
  Inbox,
  LayoutDashboard,
  MessagesSquare,
  Network,
  Package,
  Play,
  Plug,
  Plus,
  ShieldCheck,
  Settings,
  Square,
  UserCircle,
  Users,
  Wallet,
  Zap,
} from "lucide-react";

import { AboutPanel } from "@/components/settings/about-panel";
import { AuthPanel } from "@/components/settings/auth-panel";
import { BudgetPanel } from "@/components/settings/budget-panel";
import { TenantDefaultsPanel } from "@/components/settings/tenant-defaults-panel";
import { KeyRound, Info } from "lucide-react";

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
 * Routes shipped with the OSS build. The v2 IA packs the sidebar into
 * the three groups defined in plan §2; downstream EXP-* / SURF-* /
 * DEL-* issues will continue rewiring the target pages under these
 * declared paths.
 */
export const defaultRoutes: readonly RouteEntry[] = [
  // ----- Overview ---------------------------------------------------
  {
    path: "/",
    label: "Dashboard",
    icon: LayoutDashboard,
    navSection: "overview",
    orderHint: 10,
    keywords: ["home", "overview", "summary"],
    description: "Units, agents, and recent activity at a glance.",
  },
  {
    path: "/activity",
    label: "Activity",
    icon: Activity,
    navSection: "overview",
    orderHint: 20,
    keywords: ["events", "log", "stream", "audit"],
    description: "Raw activity event stream with filters.",
  },
  {
    path: "/analytics",
    label: "Analytics",
    icon: BarChart3,
    navSection: "overview",
    orderHint: 30,
    keywords: [
      "costs",
      "throughput",
      "waits",
      "charts",
      "spring analytics",
    ],
    description:
      "Deep-dive charts: cost, throughput, and wait-time breakdowns.",
  },

  // ----- Orchestrate ------------------------------------------------
  // orderHint lives in one global sequence so the merged-registry sort
  // (by orderHint alone, across clusters) preserves the declared
  // Overview → Orchestrate → Control reading order when both are
  // equivalent. The cluster bucketing is separate: the sidebar groups
  // by `navSection` before it renders.
  {
    path: "/units",
    label: "Units",
    icon: Network,
    navSection: "orchestrate",
    orderHint: 40,
    keywords: ["teams", "groups", "agents", "explorer", "spring unit list"],
    description:
      "Canonical Explorer — units, agents, policies, and memory in one tree.",
  },
  {
    path: "/inbox",
    label: "Inbox",
    icon: Inbox,
    navSection: "orchestrate",
    orderHint: 50,
    keywords: [
      "awaiting",
      "pending",
      "human",
      "spring inbox list",
    ],
    description: "Engagements awaiting a response from you.",
  },
  {
    path: "/discovery",
    label: "Discovery",
    icon: Compass,
    navSection: "orchestrate",
    orderHint: 60,
    keywords: [
      "expertise",
      "domains",
      "search",
      "capabilities",
      "directory",
      "spring agent expertise",
      "spring unit expertise",
    ],
    description:
      "Browse and search expertise declared by every agent and unit.",
  },

  // ----- Control ----------------------------------------------------
  {
    path: "/connectors",
    label: "Connectors",
    icon: Plug,
    navSection: "control",
    orderHint: 70,
    keywords: ["integrations", "github", "webhook", "spring connector catalog"],
    description:
      "Connector catalog, bindings, and credential health.",
  },
  {
    path: "/policies",
    label: "Policies",
    icon: ShieldCheck,
    navSection: "control",
    orderHint: 80,
    keywords: ["rollup", "routing", "boundary", "rules"],
    description: "Tenant-wide policy rollup across every unit.",
  },
  {
    path: "/budgets",
    label: "Budgets",
    icon: Wallet,
    navSection: "control",
    orderHint: 90,
    keywords: ["cost", "spend", "limits"],
    description: "Tenant-wide and per-unit spend caps.",
  },
  {
    path: "/settings",
    label: "Settings",
    icon: Settings,
    navSection: "control",
    orderHint: 100,
    keywords: [
      "tenant",
      "defaults",
      "account",
      "about",
      "packages",
      "skills",
      "agent-runtimes",
      "system configuration",
    ],
    description:
      "Tenant defaults, account, packages, skills, agent runtimes, and system configuration.",
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
    label: "Open Explorer",
    icon: Network,
    section: "actions",
    orderHint: 20,
    keywords: [
      "spring unit list",
      "units",
      "agents",
      "tree",
      "explorer",
    ],
    description:
      "Open the canonical `/units` Explorer — the single surface for units and agents.",
    href: "/units",
  },
  {
    id: "agent.list",
    label: "List agents",
    icon: Users,
    section: "actions",
    orderHint: 22,
    keywords: ["spring agent list", "roster", "directory"],
    description:
      "Browse every agent across every unit (Explorer → Agents tab).",
    href: "/units",
  },
  {
    id: "unit.start",
    label: "Start a unit",
    icon: Play,
    section: "actions",
    orderHint: 30,
    keywords: ["spring unit start", "run"],
    description: "Go to the Explorer to start a unit.",
    href: "/units",
  },
  {
    id: "unit.stop",
    label: "Stop a unit",
    icon: Square,
    section: "actions",
    orderHint: 40,
    keywords: ["spring unit stop", "halt"],
    description: "Go to the Explorer to stop a unit.",
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
    id: "thread.list",
    label: "List engagements",
    icon: MessagesSquare,
    section: "actions",
    orderHint: 55,
    keywords: ["spring thread list", "engagements", "threads", "messages"],
    description:
      "Browse message threads (Explorer → Messages tab on any unit or agent).",
    href: "/units",
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
    description: "Engagements awaiting a response from you.",
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
    keywords: ["autonomy", "policy", "initiative"],
    description:
      "Edit a unit's initiative policy (Explorer → Policies tab).",
    href: "/units",
  },
  {
    id: "packages.browse",
    label: "Browse packages",
    icon: Package,
    section: "actions",
    orderHint: 80,
    keywords: ["spring package list", "templates", "catalog"],
    description: "List installed packages in Settings.",
    href: "/settings",
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
    id: "discovery.expertise",
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
      "directory",
    ],
    description:
      "Search the tenant's expertise directory across every agent and unit.",
    href: "/discovery",
  },
  {
    id: "settings.open",
    label: "Open Settings",
    icon: Settings,
    section: "actions",
    orderHint: 110,
    keywords: [
      "tenant",
      "defaults",
      "account",
      "about",
      "system configuration",
      "packages",
      "skills",
      "agent runtimes",
    ],
    description:
      "Tenant defaults, account, packages, skills, agent runtimes, and system configuration.",
    href: "/settings",
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
    // #615: tenant-default LLM credentials. Units inherit these unless
    // they override with a same-name unit-scoped secret (Secrets tab).
    // Matches the `spring secret --scope tenant` CLI primitive.
    id: "tenant-defaults",
    label: "Tenant defaults",
    icon: KeyRound,
    description: "LLM credentials inherited by every unit in the tenant.",
    orderHint: 15,
    component: <TenantDefaultsPanel />,
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
