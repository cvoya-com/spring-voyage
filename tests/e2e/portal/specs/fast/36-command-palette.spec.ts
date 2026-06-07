import { expect, test } from "../../fixtures/test.js";

/**
 * Command palette — keyboard-driven nav primitive (cmdk-based).
 *
 * Opens with Cmd+K / Ctrl+K. Verifies the palette renders and routes the
 * Explorer action to its target. The "Open Explorer" action (`unit.list`)
 * still hrefs the legacy `/units` route (which renders the Explorer canvas).
 */

test.describe("command palette", () => {
  test("opens with Cmd+K and routes to the Explorer", async ({ page }) => {
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

    // Filter to Explorer and select the top result with Enter. The top
    // match is the "Explorer" route (`/explorer`); selecting it lands on
    // the Explorer surface. Wait for the filtered list to have at least one
    // row before pressing Enter so the selection is real.
    await input.fill("Explorer");
    await expect(
      page.locator('[data-testid^="command-palette-item-"]').first(),
    ).toBeVisible({ timeout: 5_000 });
    await page.keyboard.press("Enter");
    await page.waitForURL(/\/(units|explorer)(\/|\?|$)/, { timeout: 10_000 });
  });
});
