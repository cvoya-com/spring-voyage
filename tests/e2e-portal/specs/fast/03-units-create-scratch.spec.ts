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
    const { unitUrl } = await createScratchUnit(page, {
      name,
      displayName: "Wizard Scratch Unit",
      description: "Created by 03-units-create-scratch.spec.ts",
    });
    expect(unitUrl).toContain(`/units/${name}`);

    // The detail page exposes the unit name in the heading.
    await expect(page.getByRole("heading", { name })).toBeVisible();

    // Cross-check via the units list.
    await page.goto("/units");
    await expect(page.getByTestId("unit-explorer-route")).toBeVisible();
    await expect(page.getByText(name).first()).toBeVisible();
  });

  test("rejects an invalid name with an inline error", async ({ page }) => {
    await page.goto("/units/create");
    // Names must match /^[a-z0-9-]+$/. Try an invalid one.
    await page.getByLabel("Name").or(page.getByRole("textbox", { name: /^name$/i })).first().fill("Has Spaces");
    await page.getByLabel("Display name").or(page.getByRole("textbox", { name: /display name/i })).first().fill("oops");
    await page.getByTestId("parent-choice-top-level").click();
    // The Next button reveals next-disabled-reason with the validation hint.
    const reason = page.getByTestId("next-disabled-reason");
    await expect(reason).toBeVisible({ timeout: 5_000 });
  });
});
