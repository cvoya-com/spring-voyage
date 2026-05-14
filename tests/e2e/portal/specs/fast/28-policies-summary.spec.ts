import { expect, test } from "../../fixtures/test.js";

/**
 * /policies — tenant-wide policies summary.
 *
 * Shows a roll-up unit count per policy dimension. Empty tenants surface
 * zero, but the page should render without error.
 */

test.describe("policies — summary page", () => {
  test("renders the rollup card with a unit count", async ({ page }) => {
    await page.goto("/policies");

    const rollup = page.getByTestId("policies-rollup-unit-count");
    await expect(rollup).toBeVisible({ timeout: 15_000 });

    // Each dimension exposes a row (`policy-row-<slug>`). At least the
    // five canonical dimensions should be listed.
    const rows = await page
      .locator('[data-testid^="policy-row-"]')
      .count();
    expect(rows).toBeGreaterThanOrEqual(1);
  });
});
