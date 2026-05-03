import { apiPost } from "../../fixtures/api.js";
import { unitName } from "../../fixtures/ids.js";
import { DEFAULT_MODEL, PROVIDER_ID, TOOL_ID } from "../../fixtures/runtime.js";
import { expect, test } from "../../fixtures/test.js";

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
    await apiPost("/api/v1/tenant/units", {
      name: parent,
      displayName: parent,
      description: "Sub-unit parent (e2e-portal)",
      tool: TOOL_ID,
      provider: PROVIDER_ID,
      model: DEFAULT_MODEL,
      hosting: "ephemeral",
      isTopLevel: true,
    });

    await page.goto("/units/create");

    // Step 1 — Source: scratch (post-#1563 the wizard always asks for a
    // source first; sub-units come from the scratch branch).
    await page.getByTestId("source-card-scratch").click();
    await page.getByRole("button", { name: /^next$/i }).click();

    // Step 2 — Identity + has-parents picker.
    await page.getByLabel("Name").or(page.getByRole("textbox", { name: /^name$/i })).first().fill(child);
    await page.getByLabel("Display name").or(page.getByRole("textbox", { name: /display name/i })).first().fill(child);
    await page.getByTestId("parent-choice-has-parents").click();
    await expect(page.getByTestId("parent-unit-picker")).toBeVisible();
    // The picker exposes a `parent-option-${unitId}` test id per option.
    // Match by the parent name appearing inside the option.
    const option = page
      .locator('[data-testid^="parent-option-"]')
      .filter({ hasText: parent })
      .first();
    await option.click();
    await page.getByRole("button", { name: /^next$/i }).click();

    // Step 3 — Execution.
    await page.getByLabel("Execution tool").selectOption(TOOL_ID);
    await page.getByLabel("LLM provider").selectOption(PROVIDER_ID);
    const modelSelect = page.getByLabel("Model");
    await modelSelect.waitFor({ state: "visible", timeout: 30_000 });
    const values = await modelSelect.evaluate((el) =>
      Array.from((el as HTMLSelectElement).options).map((o) => o.value),
    );
    if (values.length === 0) test.skip(true, "Ollama returned no models");
    const firstValue = values[0]!;
    await modelSelect.selectOption(firstValue);
    await page.getByRole("button", { name: /^next$/i }).click();

    // Step 4 — Connector (skip).
    await page.getByRole("button", { name: /skip connector|don.?t bind/i }).or(page.getByRole("button", { name: /^next$/i })).first().click();

    // Step 5 — Install (post-#1563 final step; was Finalize+Secrets pair).
    await page.getByTestId("install-unit-button").click();
    // The wizard redirects to /units after install completes; wait for
    // the navigation away from /units/create then go to the explorer's
    // deep-link for the child unit.
    await page.waitForURL((url) => !url.pathname.endsWith("/units/create"), {
      timeout: 90_000,
    });
    await page.goto(
      `/units?node=${encodeURIComponent(child)}&tab=Overview`,
    );

    // Cross-check: detail page surfaces the parent breadcrumb / banner.
    await expect(page.getByText(parent).first()).toBeVisible({ timeout: 10_000 });
  });
});
