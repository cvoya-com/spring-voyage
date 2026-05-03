import { expect, test } from "../../fixtures/test.js";

/**
 * Wizard: Catalog source branch (post-#1563 replacement for the deleted
 * "Mode = Template" path). v0.1 ships two operator-relevant catalog
 * packages without required inputs:
 *   - software-engineering → unit `engineering-team`
 *   - product-management   → unit `product-squad`
 *
 * `spring-voyage-oss` requires GitHub inputs and is exercised in the
 * killer suite. Mirrors the CLI scenario
 * `tests/cli-scenarios/scenarios/units/unit-create-from-template.sh`
 * which now drives `spring package install <name>` for the same flow.
 *
 * The catalog branch does NOT take a wizard-supplied unit name — the
 * package's manifest declares the canonical name. Cleanup is done
 * against that canonical name.
 */

test.describe("units — create from package (catalog wizard)", () => {
  test("software-engineering package → engineering-team unit", async ({ page, tracker }) => {
    const unit = "engineering-team";
    tracker.unit(unit);
    await runCatalogFlow(page, { packageName: "software-engineering", expectedUnit: unit });
    await expect(page).toHaveURL(
      new RegExp(`/units(?:\\?|$)`),
    );
  });

  test("product-management package → product-squad unit", async ({ page, tracker }) => {
    const unit = "product-squad";
    tracker.unit(unit);
    await runCatalogFlow(page, { packageName: "product-management", expectedUnit: unit });
    await expect(page).toHaveURL(
      new RegExp(`/units(?:\\?|$)`),
    );
  });
});

async function runCatalogFlow(
  page: import("@playwright/test").Page,
  opts: { packageName: string; expectedUnit: string },
): Promise<void> {
  await page.goto("/units/create");

  // Step 1 — Source: pick Catalog.
  await page.getByTestId("source-card-catalog").click();
  await page.getByRole("button", { name: /^next$/i }).click();

  // Step 2 — Package picker.
  await page.getByTestId(`package-option-${opts.packageName}`).waitFor({ timeout: 30_000 });
  await page.getByTestId(`package-option-${opts.packageName}`).click();
  await page.getByRole("button", { name: /^next$/i }).click();

  // Step 3 — Connector (skip).
  const skip = page.getByRole("button", { name: /skip connector|don.?t bind/i }).first();
  if (await skip.isVisible().catch(() => false)) {
    await skip.click();
  } else {
    await page.getByRole("button", { name: /^next$/i }).click();
  }

  // Step 4 — Install. The wizard's `installActive` effect navigates to
  // `/units` once the install reaches active terminal state; the
  // transient `install-status-failed` alert can also flash mid-staging,
  // so we wait for the URL change as the authoritative signal and only
  // inspect the failed panel as a diagnostic when the URL never changes
  // within the deadline.
  await page.getByTestId("install-unit-button").click();
  try {
    await page.waitForURL((url) => !url.pathname.endsWith("/units/create"), {
      timeout: 90_000,
    });
  } catch (err) {
    const failed = page.getByTestId("install-status-failed");
    if (await failed.isVisible().catch(() => false)) {
      const errText = (await failed.innerText().catch(() => "")) || "(no error text)";
      throw new Error(`Catalog install failed: ${errText}`);
    }
    throw err;
  }
}
