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
// the default sidebar route registry (`defaultRoutes`). Specs that compare
// what the sidebar should expose pull from this map; if the registry
// changes, update both in sync.
//
// #2473 / #2517: the old "Units" link at `/units` is now "Explorer" at
// `/explorer` (the legacy `/units` route still renders the Explorer canvas
// but is no longer the sidebar target). #2787 added "Conversations"
// (`/conversations`); #1454 surfaced "Engagement" (`/engagement`) in the
// management sidebar. `agents` has no sidebar entry — the unified Explorer
// hosts agents (#815).
export const NAV_PATHS = {
  dashboard: "/",
  explorer: "/explorer",
  activity: "/activity",
  conversations: "/conversations",
  analytics: "/analytics",
  inbox: "/inbox",
  discovery: "/discovery",
  engagement: "/engagement",
  connectors: "/connectors",
  policies: "/policies",
  budgets: "/budgets",
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
 * Mirror the portal's `toExplorerPathSegment` (`src/lib/explorer-url.ts`):
 * the canonical Explorer URL segment for a unit/agent is its id with the
 * dashes stripped when the id is a UUID (the 32-char no-dash hex). Anything
 * that isn't a UUID passes through unchanged.
 */
export function toExplorerHex(idOrHex: string): string {
  const stripped = idOrHex.replace(/-/g, "");
  if (/^[0-9a-f]{32}$/i.test(stripped)) return stripped;
  return idOrHex;
}

/**
 * Navigate straight to a unit's Explorer detail page, optionally pinned to
 * a tab / sub-tab. Accepts a dashed UUID or the 32-char hex; normalises to
 * the canonical no-dash path segment the `/explorer/units/<id>` route reads.
 */
export async function gotoExplorerUnit(
  page: Page,
  idOrHex: string,
  opts: { tab?: string; subtab?: string } = {},
): Promise<void> {
  const hex = toExplorerHex(idOrHex);
  const params = new URLSearchParams();
  if (opts.tab) params.set("tab", opts.tab);
  if (opts.subtab) params.set("subtab", opts.subtab);
  const qs = params.toString();
  await page.goto(
    `/explorer/units/${encodeURIComponent(hex)}${qs ? `?${qs}` : ""}`,
  );
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
