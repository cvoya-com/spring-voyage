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
    // Open the palette.
    const isMac = process.platform === "darwin";
    await page.keyboard.press(isMac ? "Meta+K" : "Control+K");

    // The palette is a cmdk dialog. The main testable surface is its
    // search input, which is a textbox inside a dialog.
    const dialog = page.getByRole("dialog");
    await expect(dialog).toBeVisible({ timeout: 5_000 });
    const input = dialog.getByRole("combobox").or(dialog.getByRole("textbox")).first();
    await input.fill("units");
    await page.keyboard.press("Enter");

    // After the palette routes, /units should be active.
    await page.waitForURL(/\/units(\/|\?|$)/, { timeout: 10_000 });
  });
});
