import { unitName } from "../../fixtures/ids.js";
import { expect, test } from "../../fixtures/test.js";

/**
 * Killer use case — product-management/product-squad template variant.
 * Mirror of 01-software-engineering-team.spec.ts but using the product
 * template. The two templates ship working out of the box per E2 plan.
 */

test.describe("killer use case — product management squad", () => {
  test.setTimeout(300_000);

  test("template wizard creates a product-squad and lands on detail", async ({
    page,
    tracker,
    ollamaUp,
  }) => {
    void ollamaUp;
    const name = tracker.unit(unitName("k-prod"));

    await page.goto("/units/create");

    await page.getByLabel("Name").or(page.getByRole("textbox", { name: /^name$/i })).first().fill(name);
    await page.getByLabel("Display name").or(page.getByRole("textbox", { name: /display name/i })).first().fill(name);
    await page.getByTestId("parent-choice-top-level").click();
    await page.getByRole("button", { name: /^next$/i }).click();

    await page.getByLabel("Execution tool").selectOption("dapr-agent");
    await page.getByLabel("LLM provider").selectOption("ollama");
    const modelSelect = page.getByLabel("Model");
    await modelSelect.waitFor({ state: "visible", timeout: 30_000 });
    const values = await modelSelect.evaluate((el) =>
      Array.from((el as HTMLSelectElement).options).map((o) => o.value),
    );
    if (values.length === 0) test.skip(true, "Ollama empty");
    const firstValue = values[0]!;
    await modelSelect.selectOption(firstValue);
    await page.getByRole("button", { name: /^next$/i }).click();

    await page.getByRole("button", { name: /from template/i }).click();
    await page.getByRole("button", { name: /product-squad/i }).first().click();
    await page.getByRole("button", { name: /^next$/i }).click();

    const skip = page.getByRole("button", { name: /skip connector|don.?t bind/i }).first();
    if (await skip.isVisible().catch(() => false)) {
      await skip.click();
    } else {
      await page.getByRole("button", { name: /^next$/i }).click();
    }
    await page.getByRole("button", { name: /^next$/i }).click();

    await page.getByTestId("create-unit-button").click();
    await page.waitForURL(new RegExp(`/units/${name}$`), { timeout: 180_000 });

    // The unit's Agents tab lists the seeded agents from the template.
    await page.getByRole("tab", { name: /^agents$/i }).click();
    await expect(
      page.locator('[data-testid^="unit-membership-"]').first(),
    ).toBeVisible({ timeout: 30_000 });
  });
});
