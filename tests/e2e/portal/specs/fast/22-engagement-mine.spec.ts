import { expect, test } from "../../fixtures/test.js";

/**
 * /engagement/mine — list view.
 *
 * Post-#1502 the live engagement list moved into the EngagementShell
 * sidebar (`engagement-sidebar`), and `/engagement/mine` itself renders
 * only the empty-selection prompt (`my-engagements-page`). The sidebar
 * list resolves to one of loading → {root, empty, error}; each row exposes
 * `engagement-card-<threadId>`.
 */

test.describe("engagement portal — my engagements", () => {
  test("renders the selection prompt + the sidebar list settles to a terminal state", async ({
    page,
  }) => {
    await page.goto("/engagement/mine");
    await expect(page.getByTestId("my-engagements-page")).toBeVisible();

    // The live list lives in the engagement shell sidebar.
    await expect(page.getByTestId("engagement-sidebar")).toBeVisible();

    // The list renders one of three terminal states. Tolerate any.
    const list = page.getByTestId("engagement-list-root");
    const empty = page.getByTestId("engagement-list-empty");
    const error = page.getByTestId("engagement-list-error");
    const loading = page.getByTestId("engagement-list-loading");

    // Wait until something other than the loading skeleton is showing.
    await expect(loading).toBeHidden({ timeout: 30_000 });
    expect(
      (await list.isVisible().catch(() => false)) ||
        (await empty.isVisible().catch(() => false)) ||
        (await error.isVisible().catch(() => false)),
      "expected one of {engagement-list-root, -empty, -error} to be visible",
    ).toBe(true);
  });
});
