import type { Page } from "@playwright/test";

/**
 * Sidebar navigation helpers — keyed off the `data-testid="sidebar-nav-link-<path>"`
 * scheme exposed by `src/Cvoya.Spring.Web/src/components/sidebar.tsx`.
 *
 * Going through the sidebar (rather than `page.goto`) verifies that the
 * route is exposed in the management portal's IA. Specs that don't care
 * about IA can `page.goto(...)` directly.
 */

// Mirrors `src/Cvoya.Spring.Web/src/lib/extensions/defaults.tsx` —
// the default sidebar route registry. Specs that compare what the
// sidebar should expose pull from this map; if the registry changes,
// update both in sync. `agents` and `engagement` are intentionally
// absent — the unified Units explorer hosts agents (#815), and the
// engagement portal lives at its own origin/path outside the
// management sidebar.
export const NAV_PATHS = {
  dashboard: "/",
  units: "/units",
  inbox: "/inbox",
  activity: "/activity",
  analytics: "/analytics",
  policies: "/policies",
  budgets: "/budgets",
  connectors: "/connectors",
  discovery: "/discovery",
  settings: "/settings",
} as const;

export type NavKey = keyof typeof NAV_PATHS;

export async function clickSidebar(page: Page, key: NavKey): Promise<void> {
  // The portal renders both a mobile drawer and a desktop sidebar — both
  // carry every `sidebar-nav-link-*` testid. Filter to the visible one
  // (under `Desktop Chrome` that's the desktop sidebar).
  await page
    .locator(`[data-testid="sidebar-nav-link-${NAV_PATHS[key]}"]:visible`)
    .first()
    .click();
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
 *
 * `sidebar-header` is rendered twice in the DOM at all times (the
 * mobile drawer + the desktop sidebar; their visibility is toggled by
 * media queries), so a bare `getByTestId("sidebar-header")` would trip
 * Playwright's strict-mode guard. Filter to the visible one — under
 * the `Desktop Chrome` device used by every project, that's the
 * desktop sidebar.
 */
export async function waitForShell(page: Page): Promise<void> {
  await page.getByTestId("skip-to-main").waitFor({ state: "attached" });
  await page
    .locator('[data-testid="sidebar-header"]:visible')
    .first()
    .waitFor({ state: "visible" });
}
