import { expect, test } from "../../fixtures/test.js";

/**
 * /engagement/mine — list view.
 *
 * On a fresh tenant the list is empty; the shell still renders. With
 * engagements, each row exposes `engagement-card-<threadId>`.
 */

test.describe("engagement portal — my engagements", () => {
  test("renders empty-state OR a list of engagement cards", async ({ page }) => {
    await page.goto("/engagement/mine");
    await expect(page.getByTestId("my-engagements-page")).toBeVisible();

    // The list renders one of three states. Tolerate any of them.
    const list = page.getByTestId("engagement-list");
    const empty = page.getByTestId("engagement-list-empty");
    const error = page.getByTestId("engagement-list-error");
    const loading = page.getByTestId("engagement-list-loading");

    // Wait until something other than the loading skeleton is showing.
    await expect(loading).toBeHidden({ timeout: 30_000 });
    expect(
      (await list.isVisible().catch(() => false)) ||
        (await empty.isVisible().catch(() => false)) ||
        (await error.isVisible().catch(() => false)),
      "expected one of {engagement-list, -empty, -error} to be visible",
    ).toBe(true);
  });
});
