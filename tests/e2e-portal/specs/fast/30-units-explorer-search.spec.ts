import { apiPost } from "../../fixtures/api.js";
import { unitName } from "../../fixtures/ids.js";
import { DEFAULT_MODEL, PROVIDER_ID, TOOL_ID } from "../../fixtures/runtime.js";
import { expect, test } from "../../fixtures/test.js";

/**
 * /units — explorer/list page filtering and the "New unit" affordance.
 */

test.describe("units — explorer", () => {
  test("explorer renders the route, exposes a 'New unit' CTA, and shows seeded units", async ({
    page,
    tracker,
  }) => {
    const a = tracker.unit(unitName("explorer-a"));
    const b = tracker.unit(unitName("explorer-b"));
    for (const n of [a, b]) {
      await apiPost("/api/v1/tenant/units", {
        name: n,
        displayName: n,
        description: `Explorer spec (e2e-portal): ${n}`,
        tool: TOOL_ID,
        provider: PROVIDER_ID,
        model: DEFAULT_MODEL,
        hosting: "ephemeral",
        isTopLevel: true,
      });
    }

    await page.goto("/units");
    await expect(page.getByTestId("unit-explorer-route")).toBeVisible();
    await expect(page.getByTestId("units-page-new-unit")).toBeVisible();

    // Both seeded units must appear in the explorer.
    await expect(page.getByText(a).first()).toBeVisible({ timeout: 15_000 });
    await expect(page.getByText(b).first()).toBeVisible({ timeout: 15_000 });
  });
});
