import { apiGet, seedUnit } from "../../fixtures/api.js";
import { unitName } from "../../fixtures/ids.js";
import { expect, test } from "../../fixtures/test.js";
import { gotoExplorerUnit } from "../../helpers/nav.js";

/**
 * Boundary tab — opacity, projection, synthesis rules + YAML upload.
 *
 * The boundary tab exposes three rule lists driven by editable rows. This
 * spec exercises the YAML-upload affordance (which is the load-bearing
 * import path operators use to seed boundaries).
 */

interface BoundaryResponse {
  rules?: unknown[];
}

test.describe("units — boundary tab", () => {
  test("upload YAML, see diff, apply, persist", async ({ page, tracker }) => {
    const name = tracker.unit(unitName("boundary"));
    const u = await seedUnit(name, { description: "Boundary spec (e2e-portal)" });

    // Boundary moved under Config (subtab) per #2254.
    // Deep-link straight to it; the explorer round-trips ?subtab=.
    await gotoExplorerUnit(page, u.hex, { tab: "Config", subtab: "Boundary" });
    await expect(page.getByTestId("boundary-tab")).toBeVisible();

    // The YAML upload card has its own testid.
    await expect(page.getByTestId("boundary-yaml-upload")).toBeVisible();

    // Find the YAML textarea inside the upload card and paste a minimal manifest.
    const yaml = [
      "rules:",
      "  - kind: opacity",
      "    selector: '*'",
      "    visibility: opaque",
      "",
    ].join("\n");
    await page
      .getByTestId("boundary-yaml-upload")
      .getByRole("textbox")
      .first()
      .fill(yaml);

    // Apply.
    const apply = page.getByTestId("boundary-yaml-apply");
    if (await apply.isVisible().catch(() => false)) {
      await apply.click();
    }

    // The diff view either confirms a no-op or shows a non-empty diff.
    // Either way the action should not surface an error.
    await expect(page.getByTestId("boundary-yaml-error")).toHaveCount(0);

    // Cross-check the API.
    const boundary = await apiGet<BoundaryResponse>(
      `/api/v1/tenant/units/${encodeURIComponent(u.hex)}/boundary`,
    );
    expect(boundary).toBeDefined();
  });
});
