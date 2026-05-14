import { expect, test } from "../../fixtures/test.js";

/**
 * Tenant secrets — settings panel.
 *
 * The portal's Tenant defaults panel exposes a fixed set of provider
 * credential slots (anthropic-api-key, openai-api-key, google-api-key);
 * arbitrary tenant-scoped secret names are CLI-only by design (per
 * AGENTS.md "Operator surfaces" rule). This spec drives the
 * set → clear path on one slot to cover the read-only-view-plus-set
 * contract. Mirrors `tests/e2e/scenarios/fast/21-secret-cli.sh` only on
 * shape — the portal cannot create arbitrary tenant secrets.
 */

const SLOT_NAME = "google-api-key";

test.describe("settings — tenant secrets", () => {
  test("set + clear the google-api-key tenant default", async ({
    page,
    tracker,
  }) => {
    // Track the slot for cleanup so a leaked Set doesn't bleed into
    // sibling specs.
    tracker.tenantSecret(SLOT_NAME);

    await page.goto("/settings");

    const row = page.getByTestId(`tenant-default-${SLOT_NAME}`);
    await expect(row).toBeVisible({ timeout: 10_000 });

    // Skip if the slot is already set — another spec may have left it
    // dirty; the explicit clear at the end of this test resets it.
    if (await row.getByText(/\bset\b/).isVisible().catch(() => false)) {
      const initialClear = row.getByRole("button", {
        name: new RegExp(`clear ${SLOT_NAME.replace(/-/g, " ")}|clear`, "i"),
      });
      if (await initialClear.first().isVisible().catch(() => false)) {
        await initialClear.first().click();
        await expect(row.getByText(/\bunset\b/)).toBeVisible({
          timeout: 5_000,
        });
      }
    }

    // Type a placeholder value and click Set.
    await row.getByPlaceholder(/^Value$|^New value/).fill("placeholder-not-real");
    await row.getByRole("button", { name: /^Set\b|Rotate/i }).click();

    // The row flips to the "set" badge.
    await expect(row.getByText(/\bset\b/).first()).toBeVisible({
      timeout: 10_000,
    });

    // Clear the slot. The button is aria-labelled "Clear <label>".
    await row
      .getByRole("button", { name: /^Clear / })
      .first()
      .click();
    await expect(row.getByText(/\bunset\b/).first()).toBeVisible({
      timeout: 10_000,
    });
  });
});
