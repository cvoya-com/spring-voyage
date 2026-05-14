import { expect, test } from "../../fixtures/test.js";

/**
 * Activity feed — page renders, sparkline placeholder eventually swaps to data.
 *
 * Mirrors the read-side of `tests/e2e/scenarios/fast/17-activity-query-filters.sh`.
 */

test.describe("activity feed", () => {
  test("renders without error, sparkline placeholder/data is present", async ({ page }) => {
    await page.goto("/activity");

    const sparkline = page.getByTestId("activity-sparkline");
    const placeholder = page.getByTestId("activity-sparkline-placeholder");
    const visible =
      (await sparkline.isVisible().catch(() => false)) ||
      (await placeholder.isVisible().catch(() => false));
    expect(visible, "expected activity-sparkline or its placeholder to render").toBe(true);
  });
});
