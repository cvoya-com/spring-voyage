import { expect, test } from "../../fixtures/test.js";

/**
 * Command palette — keyboard-driven nav primitive (cmdk-based).
 *
 * Opens with Cmd+K / Ctrl+K. Verifies the palette renders and routes a
 * search to a primary nav target.
 */

test.describe("command palette", () => {
  test("opens with Cmd+K and routes to /units", async ({ page }) => {
    await page.goto("/");
    // The keyboard handler is a window-level listener; click the body
    // first so the active element is something stable, then send the
    // shortcut. Try both Meta+K and Control+K because the testing
    // browser doesn't always honor `process.platform` for shortcuts.
    await page.locator("body").click();
    await page.keyboard.press("Meta+k");
    const input = page.getByTestId("command-palette-input");
    if (!(await input.isVisible().catch(() => false))) {
      await page.keyboard.press("Control+k");
    }
    await expect(input).toBeVisible({ timeout: 5_000 });

    await input.fill("units");
    await page.keyboard.press("Enter");
    await page.waitForURL(/\/units(\/|\?|$)/, { timeout: 10_000 });
  });
});
