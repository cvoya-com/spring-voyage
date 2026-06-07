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

  test("/settings/model-providers lists the installed model providers (ADR-0038)", async ({ page }) => {
    await page.goto("/settings/model-providers");
    await expect(page.getByRole("heading", { name: /model.?providers?/i }).first()).toBeVisible();

    // The OSS dev environment installs at least anthropic + ollama
    // by default; match either to keep the assertion lenient.
    await expect(page.getByText(/anthropic|ollama/i).first()).toBeVisible({ timeout: 10_000 });
  });

  test("/settings/skills renders the skills registry", async ({ page }) => {
    await page.goto("/settings/skills");
    await expect(page.getByTestId("settings-skills-list")).toBeVisible({ timeout: 10_000 });
  });

  test("/settings/packages lists installed packages", async ({ page }) => {
    await page.goto("/settings/packages");
    await expect(page.getByRole("heading", { name: /packages?/i }).first()).toBeVisible();
    // Built-in packages from packages/ surface as cards keyed on the
    // package name (`package-card-<name>`); the visible label is the
    // friendly displayName ("Software Engineering" / "Spring Voyage OSS").
    await expect(
      page
        .getByTestId("package-card-software-engineering")
        .or(page.getByTestId("package-card-spring-voyage-oss"))
        .first(),
    ).toBeVisible({ timeout: 10_000 });
  });

  test("/settings/system-configuration renders without error", async ({ page }) => {
    await page.goto("/settings/system-configuration");
    await expect(page.getByRole("heading").first()).toBeVisible({ timeout: 10_000 });
  });
});
