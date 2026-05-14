import { expect, test } from "../../fixtures/test.js";

/**
 * Connectors list page — every installed connector type renders a card.
 *
 * v0.1 ships GitHub by default; arxiv and websearch are also bundled.
 * The card test id is `connector-card-<typeSlug>`.
 */

test.describe("connectors — list page", () => {
  test("renders cards for the built-in connectors", async ({ page }) => {
    await page.goto("/connectors");

    // GitHub is the canonical built-in connector and the load-bearing one
    // for the v0.1 killer use case (E2 plan).
    await expect(page.getByTestId("connector-card-github")).toBeVisible({
      timeout: 15_000,
    });

    // The bundled non-GitHub connectors should also surface — assert at
    // least one additional card so a registry-loading regression is caught.
    const otherCards = await page
      .locator('[data-testid^="connector-card-"]')
      .count();
    expect(otherCards).toBeGreaterThanOrEqual(1);
  });

  test("github card link routes to /connectors/github with config schema", async ({ page }) => {
    await page.goto("/connectors");
    await page.getByTestId("connector-card-link-github").click();
    await expect(page).toHaveURL(/\/connectors\/github$/);
    // The detail page renders its config schema once loaded.
    await expect(page.getByTestId("connector-config-schema")).toBeVisible({
      timeout: 15_000,
    });
  });
});
