import { expect, test } from "../../fixtures/test.js";

/**
 * Activity feed — page renders, sparkline placeholder eventually swaps to data.
 *
 * `/activity` redirects to the Events tab (`/activity/events`, #2867); the
 * sparkline + its loading placeholder live there.
 *
 * Mirrors the read-side of `tests/e2e/scenarios/fast/17-activity-query-filters.sh`.
 */

test.describe("activity feed", () => {
  test("renders without error, sparkline placeholder/data is present", async ({ page }) => {
    await page.goto("/activity");
    // `/activity` → `/activity/events`; wait for the redirect to settle.
    await page.waitForURL(/\/activity\/events/, { timeout: 10_000 });

    // The sparkline renders once activity data resolves; until then a
    // placeholder holds its space. Either is a valid "rendered" state.
    const sparklineOrPlaceholder = page
      .getByTestId("activity-sparkline")
      .or(page.getByTestId("activity-sparkline-placeholder"));
    await expect(
      sparklineOrPlaceholder.first(),
      "expected activity-sparkline or its placeholder to render",
    ).toBeVisible({ timeout: 15_000 });
  });
});
