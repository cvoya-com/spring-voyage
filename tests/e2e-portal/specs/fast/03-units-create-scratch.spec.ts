import { unitName } from "../../fixtures/ids.js";
import { expect, test } from "../../fixtures/test.js";
import { createScratchUnit } from "../../helpers/unit-wizard.js";

/**
 * Wizard: 6-step "from scratch" flow with the dapr-agent + ollama runtime
 * pin. The shell counterpart is `tests/e2e/scenarios/fast/02-create-unit-scratch.sh`.
 */

test.describe("units — create from scratch (wizard)", () => {
  test("creates a top-level unit; lands on /units/<name> with the unit visible", async ({
    page,
    tracker,
  }) => {
    const name = tracker.unit(unitName("wiz-scratch"));
    const displayName = "Wizard Scratch Unit";
    const { unitUrl } = await createScratchUnit(page, {
      name,
      displayName,
      description: "Created by 03-units-create-scratch.spec.ts",
    });
    expect(unitUrl).toContain(`node=${name}`);

    // The detail pane heading carries the unit's displayName via the
    // `detail-title` h1 (the explorer hydrates the TreeNode's `name`
    // prop with displayName for unit nodes; the slug appears in
    // address-copy controls instead).
    await expect(page.getByTestId("detail-title")).toContainText(displayName);

    // Cross-check via the units list — the displayName shows in the
    // tree row.
    await page.goto("/units");
    await expect(page.getByTestId("unit-explorer-route")).toBeVisible();
    await expect(
      page.getByRole("treeitem", { name: new RegExp(displayName, "i") }).first(),
    ).toBeVisible();
  });

  test("rejects an invalid name with an inline error", async ({ page }) => {
    await page.goto("/units/create");
    // Step 1 — Source: scratch.
    await page.getByTestId("source-card-scratch").click();
    await page.getByRole("button", { name: /^next$/i }).click();
    // Step 2 — Identity. Names must match /^[a-z0-9-]+$/. Try an invalid one.
    await page.getByLabel("Name").or(page.getByRole("textbox", { name: /^name$/i })).first().fill("Has Spaces");
    await page.getByLabel("Display name").or(page.getByRole("textbox", { name: /display name/i })).first().fill("oops");
    await page.getByTestId("parent-choice-top-level").click();
    // Post-#1563 the wizard surfaces the URL-safe rule as a static
    // helper text under the Name field and gates progress by disabling
    // the Next button when validation fails — no click-to-error
    // banner. Assert both:
    //   - the helper text is visible (informational), and
    //   - the Next button is disabled (the actionable block on progress).
    await expect(
      page.getByText(/Lowercase letters, digits, and hyphens only/i).first(),
    ).toBeVisible({ timeout: 5_000 });
    await expect(
      page.getByRole("button", { name: /^next$/i }),
    ).toBeDisabled();
  });
});
