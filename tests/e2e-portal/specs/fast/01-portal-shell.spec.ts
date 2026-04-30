import { expect, test } from "../../fixtures/test.js";
import { NAV_PATHS, clickSidebar, expectAtRoute, waitForShell } from "../../helpers/nav.js";

/**
 * Portal-shell boot sequence.
 *
 * Distinct from `src/Cvoya.Spring.Web/e2e/dashboard-smoke.spec.ts` (which runs
 * against an unreachable API to assert hydration). This spec runs against a
 * live stack and exercises every top-level nav target — proves the shell
 * boots AND the route exists in the management portal's IA.
 */

test.describe("portal shell", () => {
  test("dashboard hydrates and the sidebar exposes every primary nav target", async ({
    page,
  }) => {
    await page.goto("/");
    await waitForShell(page);

    // Top-level info we expect on the dashboard.
    await expect(page.getByTestId("dashboard-new-unit")).toBeVisible();
    await expect(page.getByTestId("top-level-units")).toBeAttached();

    // Every NAV_PATHS entry should resolve to a clickable sidebar link.
    for (const key of Object.keys(NAV_PATHS) as (keyof typeof NAV_PATHS)[]) {
      const path = NAV_PATHS[key];
      await expect(
        page.getByTestId(`sidebar-nav-link-${path}`),
        `sidebar link missing for ${key} (${path})`,
      ).toBeVisible();
    }
  });

  test("sidebar navigation transitions to each primary route without console errors", async ({
    page,
  }) => {
    const errors: string[] = [];
    page.on("pageerror", (e) => errors.push(`pageerror: ${e.message}`));
    page.on("console", (m) => {
      if (m.type() === "error") errors.push(`console.error: ${m.text()}`);
    });

    await page.goto("/");
    await waitForShell(page);

    const targets: (keyof typeof NAV_PATHS)[] = [
      "units",
      "agents",
      "inbox",
      "activity",
      "analytics",
      "policies",
      "connectors",
      "discovery",
      "settings",
    ];
    for (const key of targets) {
      await clickSidebar(page, key);
      await expectAtRoute(page, NAV_PATHS[key]);
      await expect(page.getByRole("navigation").first()).toBeVisible();
    }

    // Filter out fetch-related noise (Playwright re-uses the same page; if any
    // network call to a hibernating service blips, that's not a portal regression).
    const fatal = errors.filter(
      (msg) =>
        !/Failed to fetch|ERR_CONNECTION|net::|fetch/i.test(msg),
    );
    expect(fatal, `unexpected client-side errors:\n${fatal.join("\n")}`).toEqual([]);
  });

  test("dark mode toggle persists across reloads", async ({ page }) => {
    await page.goto("/");
    await waitForShell(page);
    const toggle = page.getByTestId("sidebar-theme-toggle");
    await toggle.click();
    const themeAfter = await page.evaluate(() =>
      document.documentElement.getAttribute("data-theme") ??
      document.documentElement.classList.contains("dark") ? "dark" : "light",
    );
    await page.reload();
    await waitForShell(page);
    const themePersisted = await page.evaluate(() =>
      document.documentElement.getAttribute("data-theme") ??
      document.documentElement.classList.contains("dark") ? "dark" : "light",
    );
    expect(themePersisted).toBe(themeAfter);
  });
});
