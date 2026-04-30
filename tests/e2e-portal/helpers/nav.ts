import type { Page } from "@playwright/test";

/**
 * Sidebar navigation helpers — keyed off the `data-testid="sidebar-nav-link-<path>"`
 * scheme exposed by `src/Cvoya.Spring.Web/src/components/sidebar.tsx`.
 *
 * Going through the sidebar (rather than `page.goto`) verifies that the
 * route is exposed in the management portal's IA. Specs that don't care
 * about IA can `page.goto(...)` directly.
 */

export const NAV_PATHS = {
  dashboard: "/",
  units: "/units",
  agents: "/agents",
  inbox: "/inbox",
  activity: "/activity",
  analytics: "/analytics",
  policies: "/policies",
  budgets: "/budgets",
  connectors: "/connectors",
  discovery: "/discovery",
  settings: "/settings",
  engagement: "/engagement",
} as const;

export type NavKey = keyof typeof NAV_PATHS;

export async function clickSidebar(page: Page, key: NavKey): Promise<void> {
  await page.getByTestId(`sidebar-nav-link-${NAV_PATHS[key]}`).click();
}

export async function expectAtRoute(page: Page, path: string): Promise<void> {
  await page.waitForURL((url) => url.pathname.startsWith(path), {
    timeout: 10_000,
  });
}

/**
 * Wait for the portal shell to hydrate — used by the boot-sequence specs
 * to catch the "white screen because chunk failed to load" regression
 * the smoke test guards against.
 */
export async function waitForShell(page: Page): Promise<void> {
  await page.getByTestId("skip-to-main").waitFor({ state: "attached" });
  await page.getByTestId("sidebar-header").waitFor({ state: "visible" });
}
