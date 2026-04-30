import { expect, test } from "@playwright/test";

/**
 * Boots-and-renders smoke tests for the dashboard's production build.
 *
 * Scope: catch regressions vitest can't see — Turbopack chunk-graph
 * misses, hydration mismatches, font loading misconfig, broken nav
 * primitives in a real browser. We deliberately don't assert on
 * data-bound UI because the API isn't reachable in this run; instead
 * we assert on the shell (sidebar nav, headings, skip-to-main link)
 * and on a route transition.
 */

test.describe("dashboard shell smoke", () => {
  test("homepage boots and renders the sidebar + main heading", async ({
    page,
  }) => {
    const consoleErrors: string[] = [];
    page.on("pageerror", (err) => consoleErrors.push(err.message));
    page.on("console", (msg) => {
      if (msg.type() === "error") consoleErrors.push(msg.text());
    });

    await page.goto("/");

    // Skip-to-main link is part of the a11y contract (see
    // src/components/sidebar.tsx) — its presence proves the shell hydrated.
    await expect(page.getByTestId("skip-to-main")).toBeAttached();

    // Primary navigation landmark.
    await expect(page.getByRole("navigation").first()).toBeVisible();

    // Hard-fail on any uncaught client-side error during boot.
    //
    // The Next.js dev server proxies `/api/v1/*` to a dummy upstream
    // (`localhost:5000`) that doesn't exist in this smoke harness; the
    // proxy surfaces failures as either fetch rejections (handled
    // UI-side by React Query) or "Failed to load resource" console
    // errors with a 5xx status. Both are infra noise — the filter
    // below tolerates them so the assertion stays focused on
    // hydration / runtime bugs in the shell itself.
    const fatal = consoleErrors.filter(
      (msg) =>
        !msg.includes("Failed to fetch") &&
        !msg.includes("ERR_CONNECTION_REFUSED") &&
        !msg.includes("net::") &&
        !msg.toLowerCase().includes("fetch") &&
        !/Failed to load resource: the server responded with a status of (5\d\d|404|403)/i.test(
          msg,
        ),
    );
    expect(fatal, `unexpected client errors:\n${fatal.join("\n")}`).toEqual([]);
  });

  test("client-side route transition to /units works", async ({ page }) => {
    await page.goto("/");

    // The sidebar exposes a labelled link for each top-level route.
    // Use the link's accessible name so this test stays robust to
    // markup tweaks (icon swaps, layout reshuffles) — it only fails if
    // the route literally disappears from the nav.
    await page.getByRole("link", { name: /^units$/i }).first().click();

    // Smoke scope: the URL changed and the shell is still hydrated.
    // Don't assert on the route's data-bound content — the API is
    // unreachable in this run, so /units renders skeletons or an empty
    // state. Hydration of the layout (sidebar nav landmark) is the
    // deliverable.
    await expect(page).toHaveURL(/\/units$/);
    await expect(page.getByRole("navigation").first()).toBeVisible();
  });
});
