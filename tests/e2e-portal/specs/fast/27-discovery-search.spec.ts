import { expect, test } from "../../fixtures/test.js";

/**
 * /discovery — directory search across agents/units by expertise/skills.
 *
 * The page surfaces an empty-state on fresh tenants; this spec proves
 * the search box renders and a query doesn't break the page.
 */

test.describe("discovery — directory search", () => {
  test("page renders, search box accepts input", async ({ page }) => {
    await page.goto("/discovery");

    const search = page.getByRole("textbox", { name: /search|filter|directory/i }).first();
    await expect(search).toBeVisible({ timeout: 10_000 });
    await search.fill("rust");

    // Submit — either Enter or a Search button.
    const submit = page.getByRole("button", { name: /^(search|find|go)$/i }).first();
    if (await submit.isVisible().catch(() => false)) {
      await submit.click();
    } else {
      await search.press("Enter");
    }

    // Empty results / "no matches" copy is fine; just don't error.
    await expect(page.getByTestId("directory-error")).toHaveCount(0, { timeout: 10_000 });
  });
});
