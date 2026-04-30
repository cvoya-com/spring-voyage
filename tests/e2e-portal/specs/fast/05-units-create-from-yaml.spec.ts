import { unitName } from "../../fixtures/ids.js";
import { expect, test } from "../../fixtures/test.js";

/**
 * Wizard: create-from-YAML flow. The wizard accepts a pasted YAML
 * manifest and POSTs it to /api/v1/tenant/units/from-yaml. Mirrors the
 * shell scenario `04-create-unit-from-template.sh`'s YAML branch.
 */

test.describe("units — create from yaml (wizard)", () => {
  test("paste a minimal manifest, finalize, land on detail page", async ({
    page,
    tracker,
  }) => {
    const name = tracker.unit(unitName("yaml"));

    await page.goto("/units/create");

    // Step 1 — Identity (name still required at the wizard level for
    // routing; the YAML's `name` field has to match).
    await page.getByLabel("Name").or(page.getByRole("textbox", { name: /^name$/i })).first().fill(name);
    await page.getByLabel("Display name").or(page.getByRole("textbox", { name: /display name/i })).first().fill(name);
    await page.getByTestId("parent-choice-top-level").click();
    await page.getByRole("button", { name: /^next$/i }).click();

    // Step 2 — Execution
    await page.getByLabel("Execution tool").selectOption("dapr-agent");
    await page.getByLabel("LLM provider").selectOption("ollama");
    const modelSelect = page.getByLabel("Model");
    await modelSelect.waitFor({ state: "visible", timeout: 30_000 });
    const values = await modelSelect.evaluate((el) =>
      Array.from((el as HTMLSelectElement).options).map((o) => o.value),
    );
    if (values.length === 0) test.skip(true, "Ollama returned no models");
    const firstValue = values[0]!;
    await modelSelect.selectOption(firstValue);
    await page.getByRole("button", { name: /^next$/i }).click();

    // Step 3 — Mode = YAML
    await page.getByRole("button", { name: /from yaml|from manifest/i }).click();
    const yamlBody = [
      `apiVersion: spring/v1`,
      `kind: Unit`,
      `metadata:`,
      `  name: ${name}`,
      `  displayName: ${name}`,
      `spec:`,
      `  description: Created by 05-units-create-from-yaml.spec.ts`,
      `  execution:`,
      `    tool: dapr-agent`,
      `    provider: ollama`,
      `    model: ${firstValue}`,
      `    hosting: ephemeral`,
      ``,
    ].join("\n");
    await page.getByRole("textbox", { name: /yaml|manifest/i }).first().fill(yamlBody);
    await page.getByRole("button", { name: /^next$/i }).click();

    // Step 4 — Connector (skip)
    const skip = page.getByRole("button", { name: /skip connector|don.?t bind/i }).first();
    if (await skip.isVisible().catch(() => false)) {
      await skip.click();
    } else {
      await page.getByRole("button", { name: /^next$/i }).click();
    }

    // Step 5 — Secrets (none) → Step 6 — Finalize
    await page.getByRole("button", { name: /^next$/i }).click();
    await page.getByTestId("create-unit-button").click();

    await page.waitForURL(new RegExp(`/units/${name}$`), { timeout: 90_000 });
    await expect(page.getByRole("heading", { name })).toBeVisible();
  });
});
