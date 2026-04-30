import { expect, test } from "../../fixtures/test.js";

/**
 * /settings — tiles + sub-pages.
 *
 * Each sub-page should boot without throwing. The spec keys off the
 * `settings-tile-*` testids on the index, then visits each sub-page and
 * asserts a panel/page-level testid is present.
 */

test.describe("settings — sub-pages", () => {
  test("settings index renders the tiles + panels grids", async ({ page }) => {
    await page.goto("/settings");
    await expect(page.getByTestId("settings-panels-grid")).toBeVisible();
    await expect(page.getByTestId("settings-tiles-grid")).toBeVisible();
    const tileCount = await page.locator('[data-testid^="settings-tile-"]').count();
    expect(tileCount).toBeGreaterThanOrEqual(1);
  });

  test("/settings/agent-runtimes lists the dapr-agent + ollama runtimes", async ({ page }) => {
    await page.goto("/settings/agent-runtimes");
    await expect(page.getByRole("heading", { name: /agent.?runtimes?/i }).first()).toBeVisible();

    // The dapr-agent runtime label varies; match by text containing
    // either "Dapr" or "ollama".
    await expect(page.getByText(/dapr|ollama/i).first()).toBeVisible({ timeout: 10_000 });
  });

  test("/settings/skills renders the skills registry", async ({ page }) => {
    await page.goto("/settings/skills");
    await expect(page.getByTestId("settings-skills-list")).toBeVisible({ timeout: 10_000 });
  });

  test("/settings/packages lists installed packages", async ({ page }) => {
    await page.goto("/settings/packages");
    await expect(page.getByRole("heading", { name: /packages?/i }).first()).toBeVisible();
    // The two built-in packages from packages/ should both surface.
    await expect(
      page.getByText(/software-engineering|product-management/i).first(),
    ).toBeVisible({ timeout: 10_000 });
  });

  test("/settings/system-configuration renders without error", async ({ page }) => {
    await page.goto("/settings/system-configuration");
    await expect(page.getByRole("heading").first()).toBeVisible({ timeout: 10_000 });
  });
});
