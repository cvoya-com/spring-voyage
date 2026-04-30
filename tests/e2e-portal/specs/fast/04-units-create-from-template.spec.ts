import { unitName } from "../../fixtures/ids.js";
import { expect, test } from "../../fixtures/test.js";

/**
 * Wizard: create-from-template flow. v0.1 ships two built-in templates
 * under `packages/`:
 *   - software-engineering / engineering-team
 *   - product-management   / product-squad
 *
 * Mirrors `tests/e2e/scenarios/fast/04-create-unit-from-template.sh`.
 */

test.describe("units — create from template (wizard)", () => {
  test("software-engineering / engineering-team", async ({ page, tracker }) => {
    const name = tracker.unit(unitName("tmpl-eng"));
    await runTemplateFlow(page, {
      name,
      packageId: "software-engineering",
      templateId: "engineering-team",
    });
    await expect(page).toHaveURL(new RegExp(`/units/${name}$`));
  });

  test("product-management / product-squad", async ({ page, tracker }) => {
    const name = tracker.unit(unitName("tmpl-pm"));
    await runTemplateFlow(page, {
      name,
      packageId: "product-management",
      templateId: "product-squad",
    });
    await expect(page).toHaveURL(new RegExp(`/units/${name}$`));
  });
});

async function runTemplateFlow(
  page: import("@playwright/test").Page,
  opts: { name: string; packageId: string; templateId: string },
): Promise<void> {
  await page.goto("/units/create");

  // Step 1 — Identity
  await page.getByLabel("Name").or(page.getByRole("textbox", { name: /^name$/i })).first().fill(opts.name);
  await page.getByLabel("Display name").or(page.getByRole("textbox", { name: /display name/i })).first().fill(opts.name);
  await page.getByTestId("parent-choice-top-level").click();
  await page.getByRole("button", { name: /^next$/i }).click();

  // Step 2 — Execution (the wizard still asks for tool/model even in template
  // mode; templates can override these but the wizard form requires valid
  // values to proceed).
  await page.getByLabel("Execution tool").selectOption("dapr-agent");
  await page.getByLabel("LLM provider").selectOption("ollama");
  const modelSelect = page.getByLabel("Model");
  await modelSelect.waitFor({ state: "visible", timeout: 30_000 });
  const optionValues = await modelSelect.evaluate((el) =>
    Array.from((el as HTMLSelectElement).options).map((o) => o.value),
  );
  if (optionValues.length === 0) {
    throw new Error("Model dropdown empty; pull an Ollama model first.");
  }
  const firstValue = optionValues[0]!;
  await modelSelect.selectOption(firstValue);
  await page.getByRole("button", { name: /^next$/i }).click();

  // Step 3 — Mode = template
  await page.getByRole("button", { name: /from template/i }).click();
  // Pick the template card from the catalogue. The card label includes the
  // template's displayName; we match by package/templateId.
  await page
    .getByRole("button", { name: new RegExp(opts.templateId, "i") })
    .first()
    .click();
  await page.getByRole("button", { name: /^next$/i }).click();

  // Step 4 — Connector (skip)
  const skip = page.getByRole("button", { name: /skip connector|don.?t bind/i }).first();
  if (await skip.isVisible().catch(() => false)) {
    await skip.click();
  } else {
    await page.getByRole("button", { name: /^next$/i }).click();
  }

  // Step 5 — Secrets (none)
  await page.getByRole("button", { name: /^next$/i }).click();

  // Step 6 — Finalize
  await page.getByTestId("create-unit-button").click();
  await page.waitForURL(new RegExp(`/units/${opts.name}$`), { timeout: 90_000 });
}
