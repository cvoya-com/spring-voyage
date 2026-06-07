import { resolveUnitIdByDisplayName, seedUnit } from "../../fixtures/api.js";
import { unitName } from "../../fixtures/ids.js";
import { AGENT_ID, DEFAULT_MODEL, PROVIDER_ID } from "../../fixtures/runtime.js";
import { expect, test } from "../../fixtures/test.js";
import { gotoExplorerUnit } from "../../helpers/nav.js";

/**
 * Sub-unit creation via the wizard's parent picker (#814).
 *
 * Pre-seeds the parent unit through the API rather than the wizard so
 * this spec only exercises the parent-picker path; the wizard is already
 * covered by 03-units-create-scratch.spec.ts.
 *
 * Mirrors `tests/e2e/scenarios/fast/12-nested-units.sh`.
 */

test.describe("units — sub-unit (wizard parent picker)", () => {
  test("picks a parent unit and creates a child under it", async ({
    page,
    tracker,
  }) => {
    const parent = tracker.unit(unitName("parent"));
    const child = tracker.unit(unitName("child"));

    // Seed the parent unit directly. The portal wizard re-creates this
    // path; isolating the parent setup keeps the spec focused on the
    // picker behaviour.
    await seedUnit(parent, { description: "Sub-unit parent (e2e-portal)" });

    await page.goto("/units/create");

    // Step 1 — Source: scratch (post-#1563 the wizard always asks for a
    // source first; sub-units come from the scratch branch).
    await page.getByTestId("source-card-scratch").click();
    await page.getByRole("button", { name: /^next$/i }).click();

    // Step 2 — Identity + has-parents picker.
    await page.getByRole("textbox", { name: /^name/i }).first().fill(child);
    await page
      .getByRole("textbox", { name: /display name/i })
      .first()
      .fill(child);
    await page.getByTestId("parent-choice-has-parents").click();
    await expect(page.getByTestId("parent-unit-picker")).toBeVisible();
    // The picker exposes a `parent-option-${unitId}` test id per option;
    // each option's label carries the parent's display name.
    const option = page
      .locator('[data-testid^="parent-option-"]')
      .filter({ hasText: parent })
      .first();
    await option.click();
    await page.getByRole("button", { name: /^next$/i }).click();

    // Step 3 — Execution (runtime / provider / model via stable testids).
    await page.getByTestId("unit-create-runtime-select").selectOption(AGENT_ID);
    await page
      .getByTestId("unit-create-model-provider-select")
      .selectOption(PROVIDER_ID);
    const modelSelect = page.getByTestId("unit-create-model-select");
    await modelSelect.waitFor({ state: "visible", timeout: 30_000 });
    const values = await modelSelect.evaluate((el) =>
      Array.from((el as HTMLSelectElement).options).map((o) => o.value),
    );
    if (values.length === 0) test.skip(true, "Ollama returned no models");
    const desired = DEFAULT_MODEL;
    await modelSelect.selectOption(values.includes(desired) ? desired : values[0]!);
    await page.getByRole("button", { name: /^next$/i }).click();

    // Step 4 — Connector (skip).
    await page.getByRole("button", { name: /skip connector|don.?t bind/i }).or(page.getByRole("button", { name: /^next$/i })).first().click();

    // Step 5 — Install (post-#1563 final step; was Finalize+Secrets pair).
    await page.getByTestId("install-unit-button").click();
    // The scratch install POSTs the unit synchronously then validates
    // before its own redirect (slow on a cold stack). Poll the API for the
    // child unit, then deep-link to it ourselves.
    await expect
      .poll(async () => resolveUnitIdByDisplayName(child), {
        timeout: 90_000,
        intervals: [500, 1000, 2000],
      })
      .not.toBeNull();
    const childId = await resolveUnitIdByDisplayName(child);
    await gotoExplorerUnit(page, childId!, { tab: "Overview" });

    // Cross-check: detail page surfaces the parent breadcrumb / banner.
    await expect(page.getByText(parent).first()).toBeVisible({ timeout: 10_000 });
  });
});
