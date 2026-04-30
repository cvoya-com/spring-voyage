import { apiPost } from "../../fixtures/api.js";
import { unitName } from "../../fixtures/ids.js";
import { DEFAULT_MODEL, PROVIDER_ID, TOOL_ID } from "../../fixtures/runtime.js";
import { expect, test } from "../../fixtures/test.js";

/**
 * /units explorer renders a parent → child tree.
 *
 * Seeds two units (parent + child), navigates to /units, and asserts the
 * explorer surfaces the relationship.
 */

test.describe("units — tenant-tree explorer", () => {
  test("seeded parent + child are both visible in the explorer", async ({
    page,
    tracker,
  }) => {
    const parent = tracker.unit(unitName("tree-parent"));
    const child = tracker.unit(unitName("tree-child"));

    await apiPost("/api/v1/tenant/units", {
      name: parent,
      displayName: parent,
      description: "Tree spec parent (e2e-portal)",
      tool: TOOL_ID,
      provider: PROVIDER_ID,
      model: DEFAULT_MODEL,
      hosting: "ephemeral",
      isTopLevel: true,
    });
    await apiPost("/api/v1/tenant/units", {
      name: child,
      displayName: child,
      description: "Tree spec child (e2e-portal)",
      tool: TOOL_ID,
      provider: PROVIDER_ID,
      model: DEFAULT_MODEL,
      hosting: "ephemeral",
      parentUnitIds: [parent],
    });

    await page.goto("/units");
    await expect(page.getByTestId("unit-explorer-route")).toBeVisible();
    await expect(page.getByText(parent).first()).toBeVisible({ timeout: 15_000 });
    await expect(page.getByText(child).first()).toBeVisible({ timeout: 15_000 });
  });
});
