import { expect, test } from "../../fixtures/test.js";

/**
 * Wizard: Catalog source branch (post-#1563 replacement for the deleted
 * "Mode = Template" path).
 *
 * Every unit-installing catalog package in the OSS build now pins
 * `runtime: claude-code` / `provider: anthropic` and therefore declares a
 * required `anthropic-oauth` credential — the install fails-fast without
 * it. This suite is deliberately credential-free (dapr-agent + ollama
 * everywhere, no operator secrets), so we cannot drive a catalog install
 * to the active/redirect state without seeding an Anthropic OAuth token.
 *
 * Instead this spec exercises the credential-free-observable contract of
 * the catalog branch: selecting a package surfaces its declared credential
 * requirement on the Install step (the wizard's pre-flight
 * `PackageCredentialRequirementsPanel`, #2181). `hello-world` is used
 * because it declares the anthropic credential but NO required connector,
 * so the Connector step can be skipped to reach Install.
 *
 * The full catalog-install-to-active flow (with credentials + the GitHub
 * connector that `software-engineering` / `spring-voyage-oss` require) is
 * covered by the CLI suite, which can seed the tenant secret.
 */

test.describe("units — create from package (catalog wizard)", () => {
  test("selecting a claude-code package surfaces its credential requirement before install", async ({
    page,
  }) => {
    await page.goto("/units/create");

    // Step 1 — Source: pick Catalog.
    await page.getByTestId("source-card-catalog").click();
    await page.getByRole("button", { name: /^next$/i }).click();

    // Step 2 — Package picker. hello-world is a single-unit package with no
    // required connector.
    await page
      .getByTestId("package-option-hello-world")
      .waitFor({ timeout: 30_000 });
    await page.getByTestId("package-option-hello-world").click();
    await page.getByRole("button", { name: /^next$/i }).click();

    // Step 3 — Connector (none required for hello-world): skip.
    const skip = page
      .getByRole("button", { name: /skip connector|don.?t bind/i })
      .first();
    if (await skip.isVisible().catch(() => false)) {
      await skip.click();
    } else {
      await page.getByRole("button", { name: /^next$/i }).click();
    }

    // Step 4 — Install. The pre-flight credential panel lists the package's
    // declared (and as-yet-unsatisfied) anthropic-oauth requirement.
    const credPanel = page.getByTestId("package-credential-requirements");
    await expect(credPanel).toBeVisible({ timeout: 15_000 });
    await expect(credPanel).toContainText(/credential|anthropic/i);
    // Install is still offered — the operator can paste the token inline.
    await expect(page.getByTestId("install-unit-button")).toBeVisible();
  });
});
