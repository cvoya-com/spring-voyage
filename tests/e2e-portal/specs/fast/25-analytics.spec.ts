import { expect, test } from "../../fixtures/test.js";

/**
 * Analytics — costs / throughput / waits.
 *
 * Each sub-route renders a chart + table. On a fresh tenant the values are
 * zero, but the page must still render without error.
 *
 * Mirrors `tests/e2e/scenarios/fast/24-analytics-costs-breakdown.sh` (shape).
 */

test.describe("analytics", () => {
  test("/analytics renders the index", async ({ page }) => {
    await page.goto("/analytics");
    await expect(page.getByRole("heading", { name: /analytics/i }).first()).toBeVisible();
  });

  test("/analytics/costs renders without error", async ({ page }) => {
    await page.goto("/analytics/costs");
    await expect(page.getByRole("heading", { name: /cost/i }).first()).toBeVisible();
    // Chart container or empty-state copy.
    await expect(
      page.getByText(/no data|loading|spend|cost/i).first(),
    ).toBeVisible();
  });

  test("/analytics/throughput renders without error", async ({ page }) => {
    await page.goto("/analytics/throughput");
    await expect(page.getByRole("heading", { name: /throughput/i }).first()).toBeVisible();
  });

  test("/analytics/waits renders without error", async ({ page }) => {
    await page.goto("/analytics/waits");
    await expect(page.getByRole("heading", { name: /wait/i }).first()).toBeVisible();
  });
});
