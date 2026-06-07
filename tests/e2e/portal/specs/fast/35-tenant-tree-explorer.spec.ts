import { seedUnit } from "../../fixtures/api.js";
import { unitName } from "../../fixtures/ids.js";
import { expect, test } from "../../fixtures/test.js";

/**
 * /units explorer renders a parent → child tree.
 *
 * Seeds two units (parent + child), navigates to /units, and asserts the
 * explorer surfaces the relationship. The tree expands only the tenant
 * root by default, so the child row is collapsed under the parent until
 * the parent is expanded (`tree-twisty-<parentHex>`).
 */

test.describe("units — tenant-tree explorer", () => {
  test("seeded parent + child are both visible in the explorer", async ({
    page,
    tracker,
  }) => {
    const parent = tracker.unit(unitName("tree-parent"));
    const child = tracker.unit(unitName("tree-child"));

    const p = await seedUnit(parent, {
      description: "Tree spec parent (e2e-portal)",
    });
    const c = await seedUnit(child, {
      description: "Tree spec child (e2e-portal)",
      parentHexIds: [p.hex],
    });

    await page.goto("/units");
    await expect(page.getByTestId("unit-explorer-route")).toBeVisible();

    // The parent row is visible directly under the (expanded) tenant root.
    // Tree node ids are the 32-char no-dash hex (the seed's `hex`).
    const parentRow = page.getByTestId(`tree-row-${p.hex}`);
    await expect(parentRow).toBeVisible({ timeout: 15_000 });
    await expect(parentRow).toContainText(parent);

    // Expand the parent so its child row mounts, then assert it surfaces.
    await page.getByTestId(`tree-twisty-${p.hex}`).click();
    const childRow = page.getByTestId(`tree-row-${c.hex}`);
    await expect(childRow).toBeVisible({ timeout: 15_000 });
    await expect(childRow).toContainText(child);
  });
});
