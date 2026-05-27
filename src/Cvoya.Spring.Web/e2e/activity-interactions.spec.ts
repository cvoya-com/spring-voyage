import { expect, test } from "@playwright/test";

/**
 * Boots-and-renders smoke test for the new
 * `/activity/interactions` route (#2867). Scope is the same as the
 * dashboard smoke spec — assert shell + chrome, not data-bound UI.
 * The Playwright web-server points at a non-routable upstream so the
 * snapshot endpoint returns nothing; we assert on:
 *
 *   - The route boots without uncaught client errors.
 *   - The filter card + page heading render.
 *   - The redirect from `/activity` → `/activity/events` works (the
 *     surface split is load-bearing for the sidebar nav).
 *   - The view-mode toggle exists with the expected default ("both").
 *   - The depth dial defaults to 2 hops.
 */

test.describe("activity interactions surface", () => {
  test("redirects /activity to /activity/events", async ({ page }) => {
    await page.goto("/activity");
    await expect(page).toHaveURL(/\/activity\/events$/);
  });

  test("/activity/interactions renders the filter strip and view-mode toggle", async ({
    page,
  }) => {
    const consoleErrors: string[] = [];
    page.on("pageerror", (err) => consoleErrors.push(err.message));
    page.on("console", (msg) => {
      if (msg.type() === "error") consoleErrors.push(msg.text());
    });

    await page.goto("/activity/interactions");

    // Page chrome rendered.
    await expect(page.getByTestId("interactions-page")).toBeVisible();
    await expect(
      page.getByRole("heading", { name: /interactions/i }),
    ).toBeVisible();

    // Filter strip present.
    await expect(page.getByTestId("interaction-filters")).toBeVisible();

    // Default depth dial = 2.
    await expect(
      page.getByTestId("interaction-filters-neighbours-2"),
    ).toHaveAttribute("aria-checked", "true");

    // Default view = "both".
    await expect(
      page.getByTestId("interaction-filters-view-both"),
    ).toHaveAttribute("aria-selected", "true");

    // No uncaught client errors during boot. Same allowlist as the
    // dashboard smoke spec — the upstream API is not routable in this
    // harness so fetch / 5xx logs are infra noise.
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

  test("tab nav switches between Events and Interactions", async ({ page }) => {
    await page.goto("/activity/events");
    await expect(page.getByTestId("activity-tabs")).toBeVisible();

    await page.getByTestId("activity-tab-interactions").click();
    await expect(page).toHaveURL(/\/activity\/interactions$/);
    await expect(page.getByTestId("interactions-page")).toBeVisible();

    await page.getByTestId("activity-tab-events").click();
    await expect(page).toHaveURL(/\/activity\/events$/);
  });

  test("view-mode toggle to matrix updates the URL", async ({ page }) => {
    await page.goto("/activity/interactions");

    await page.getByTestId("interaction-filters-view-matrix").click();
    await expect(page).toHaveURL(/view=matrix/);
  });

  test("depth dial changing to 0 updates the URL", async ({ page }) => {
    await page.goto("/activity/interactions");
    await page.getByTestId("interaction-filters-neighbours-0").click();
    await expect(page).toHaveURL(/neighbours=0/);
  });
});
