import {
  packageInstallBlockedReason,
  resolveUnitIdByDisplayName,
} from "../../fixtures/api.js";
import { expect, test } from "../../fixtures/test.js";
import { gotoExplorerUnit } from "../../helpers/nav.js";

/**
 * Killer use case — product-management catalog package variant. Mirror of
 * 01-software-engineering-team.spec.ts using the product-management catalog
 * package.
 *
 * PRECONDITION (credential-free environments skip): like
 * `software-engineering`, the OSS `product-management` package pins
 * `runtime: claude-code` (a required `anthropic-oauth` credential) AND
 * declares a required `github` connector, so the install fails-fast (400)
 * without both. This credential-free suite skips with the precise blocker
 * unless the tenant already satisfies the package's requirements; the CLI
 * suite covers the install-with-credential path.
 */

test.describe("killer use case — product management squad", () => {
  test.setTimeout(300_000);

  test("catalog wizard creates a product-squad and lands on detail", async ({
    page,
    tracker,
    ollamaUp,
  }) => {
    void ollamaUp;
    // The package's manifest declares the canonical unit name.
    const unit = "product-squad";
    tracker.unit(unit);

    const blocked = await packageInstallBlockedReason("product-management");
    if (blocked) {
      test.skip(
        true,
        `Catalog install cannot complete credential-free: ${blocked}. ` +
          `Provision the credential/connector (or run the CLI suite) to exercise this flow.`,
      );
    }

    await page.goto("/units/create");
    await page.getByTestId("source-card-catalog").click();
    await page.getByRole("button", { name: /^next$/i }).click();

    await page.getByTestId("package-option-product-management").waitFor({ timeout: 30_000 });
    await page.getByTestId("package-option-product-management").click();
    await page.getByRole("button", { name: /^next$/i }).click();

    const skip = page.getByRole("button", { name: /skip connector|don.?t bind/i }).first();
    if (await skip.isVisible().catch(() => false)) {
      await skip.click();
    } else {
      await page.getByRole("button", { name: /^next$/i }).click();
    }

    // Install. Resolve the created unit's hex via the API rather than
    // racing the wizard's lifecycle-gated redirect.
    await page.getByTestId("install-unit-button").click();
    await expect
      .poll(async () => resolveUnitIdByDisplayName(unit), {
        timeout: 90_000,
        intervals: [2000, 5000, 10_000],
      })
      .not.toBeNull();
    const unitId = await resolveUnitIdByDisplayName(unit);

    // The unit's Members tab lists the seeded agents from the package.
    // Cache invalidation between install and the membership query can be
    // eventually consistent; reload once if the first render is empty.
    await gotoExplorerUnit(page, unitId!, { tab: "Members" });
    const membership = page.locator('[data-testid^="unit-membership-"]').first();
    try {
      await expect(membership).toBeVisible({ timeout: 30_000 });
    } catch {
      await page.reload();
      await expect(membership).toBeVisible({ timeout: 30_000 });
    }
  });
});
