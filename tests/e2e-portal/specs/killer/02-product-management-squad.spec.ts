import { expect, test } from "../../fixtures/test.js";

/**
 * Killer use case — product-management catalog package variant.
 * Mirror of 01-software-engineering-team.spec.ts but using the
 * product-management catalog package; both ship working out of the box
 * per the E2 plan. Replaces the deleted "Mode = Template" path (#1583).
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

    await page.getByTestId("install-unit-button").click();
    // The wizard's `installActive` effect navigates to `/units` once the
    // install reaches the active terminal state. The transient
    // `install-status-failed` alert can also flash mid-staging on the
    // way to active, so we wait for the URL change as the authoritative
    // signal and only inspect the failed panel as a diagnostic when
    // the URL never changes within the deadline.
    try {
      await page.waitForURL((url) => !url.pathname.endsWith("/units/create"), {
        timeout: 90_000,
      });
    } catch (err) {
      const failed = page.getByTestId("install-status-failed");
      if (await failed.isVisible().catch(() => false)) {
        const errText = (await failed.innerText().catch(() => "")) || "(no error text)";
        throw new Error(`Catalog install failed on the wizard: ${errText}`);
      }
      throw err;
    }

    // The unit's Agents tab lists the seeded agents from the package.
    // Cache invalidation between install and tab membership query can
    // be eventually consistent; reload once if the first render is empty.
    await page.goto(`/units?node=${encodeURIComponent(unit)}&tab=Agents`);
    const membership = page.locator('[data-testid^="unit-membership-"]').first();
    try {
      await expect(membership).toBeVisible({ timeout: 30_000 });
    } catch {
      await page.reload();
      await expect(membership).toBeVisible({ timeout: 30_000 });
    }
  });
});
