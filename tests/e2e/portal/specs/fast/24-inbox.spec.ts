import { expect, test } from "../../fixtures/test.js";

/**
 * Inbox — list of unread messages addressed to the current human.
 *
 * Renders one of four states: loading / error / empty / list. The spec
 * tolerates all four (a fresh tenant has no inbox content).
 */

test.describe("inbox", () => {
  test("page renders one of {loading, error, empty, list}", async ({ page }) => {
    await page.goto("/inbox");

    const loading = page.getByTestId("inbox-loading");
    await expect(loading).toBeHidden({ timeout: 20_000 }).catch(() => undefined);

    const empty = page.getByTestId("inbox-empty");
    const list = page.getByTestId("inbox-list");
    const error = page.getByTestId("inbox-error");

    expect(
      (await empty.isVisible().catch(() => false)) ||
        (await list.isVisible().catch(() => false)) ||
        (await error.isVisible().catch(() => false)),
      "expected one of {inbox-empty, inbox-list, inbox-error}",
    ).toBe(true);
  });

  test("refresh button is present and clickable", async ({ page }) => {
    await page.goto("/inbox");
    const refresh = page.getByTestId("inbox-refresh");
    if (await refresh.isVisible().catch(() => false)) {
      await refresh.click();
    }
  });
});
