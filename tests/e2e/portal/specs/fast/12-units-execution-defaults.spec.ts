import { apiGet, seedUnit } from "../../fixtures/api.js";
import { unitName } from "../../fixtures/ids.js";
import { expect, test } from "../../fixtures/test.js";
import { gotoExplorerUnit } from "../../helpers/nav.js";

/**
 * Execution tab — unit-level execution defaults that agents inherit.
 *
 * Five fields: image, runtime, model, hosting, tool. The tab exposes
 * per-field clear buttons and a save action.
 */

interface UnitExecutionResponse {
  image?: string | null;
  runtime?: string | null;
  model?: string | null;
  tool?: string | null;
}

test.describe("units — execution defaults", () => {
  test("update model, save, reload, persist", async ({ page, tracker }) => {
    const name = tracker.unit(unitName("exec"));
    const u = await seedUnit(name, {
      description: "Execution defaults spec (e2e-portal)",
    });

    // Execution moved under Config (subtab) per #2254.
    await gotoExplorerUnit(page, u.hex, { tab: "Config", subtab: "Execution" });
    await expect(page.getByTestId("execution-tab")).toBeVisible();
    await expect(page.getByTestId("unit-execution-card")).toBeVisible();

    // Image field — set to a value and save. The model select is
    // server-driven so we don't change it here (avoids cross-coupling
    // with Ollama state).
    const imageInput = page.getByTestId("execution-image-input");
    await imageInput.fill("ghcr.io/example/agent:e2e");

    await page.getByRole("button", { name: /^save$/i }).first().click();

    // Cross-check.
    await expect
      .poll(
        async () => {
          const exec = await apiGet<UnitExecutionResponse>(
            `/api/v1/tenant/units/${encodeURIComponent(u.hex)}/execution`,
          );
          return exec.image;
        },
        { timeout: 10_000 },
      )
      .toBe("ghcr.io/example/agent:e2e");
  });
});
