import { expect, test } from "../../fixtures/test.js";

/**
 * Engagement-portal shell.
 *
 * /engagement → 308 → /engagement/mine. The shell exposes its own sidebar
 * (engagement-shell / engagement-sidebar / engagement-back-to-management)
 * per ADR-0033 (two-portal model).
 */

test.describe("engagement portal — shell", () => {
  test("/engagement redirects to /engagement/mine", async ({ page }) => {
    await page.goto("/engagement");
    await page.waitForURL(/\/engagement\/mine$/, { timeout: 10_000 });
    await expect(page.getByTestId("my-engagements-page")).toBeVisible();
    await expect(page.getByTestId("engagement-shell")).toBeVisible();
  });

  test("back-to-management link returns to the management portal", async ({ page }) => {
    await page.goto("/engagement/mine");
    await expect(page.getByTestId("engagement-back-to-management")).toBeVisible();
    await page.getByTestId("engagement-back-to-management").click();
    await page.waitForURL((url) => !url.pathname.startsWith("/engagement"), {
      timeout: 10_000,
    });
  });

  test("engagement sidebar + new-engagement CTA are exposed", async ({ page }) => {
    // Post-#1502 the engagement sidebar lists live threads (no static
    // nav-engagement-mine entry). The canonical entries to assert are
    // the sidebar container itself and the New Engagement CTA in the
    // header.
    await page.goto("/engagement/mine");
    await expect(page.getByTestId("engagement-sidebar")).toBeVisible();
    await expect(page.getByTestId("engagement-new-cta")).toBeVisible();
    await expect(page.getByTestId("engagement-back-to-management")).toBeVisible();
  });
});
