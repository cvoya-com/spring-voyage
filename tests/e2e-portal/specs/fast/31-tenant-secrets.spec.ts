import { secretName } from "../../fixtures/ids.js";
import { expect, test } from "../../fixtures/test.js";

/**
 * Tenant secrets — settings panel.
 *
 * Covers the create / list / delete path for tenant-scoped secrets, which
 * back tenant-default credentials inherited by every unit. Mirrors the
 * tenant-scope branch of `tests/e2e/scenarios/fast/21-secret-cli.sh`.
 */

test.describe("settings — tenant secrets", () => {
  test("create + list + delete a tenant-scoped secret", async ({
    page,
    tracker,
  }) => {
    const name = tracker.tenantSecret(secretName("tenant"));

    await page.goto("/settings");

    // Tenant defaults panel — find by accessible name; testid varies.
    const panel = page
      .getByText(/tenant defaults|tenant secrets|default credentials/i)
      .first();
    if (await panel.isVisible().catch(() => false)) {
      await panel.click();
    }

    // Create — find the affordance by label.
    const create = page
      .getByRole("button", { name: /^(add secret|new secret|create secret|add tenant secret)$/i })
      .first();
    await create.click();
    await page.getByLabel(/secret name|name/i).first().fill(name);
    await page.getByLabel(/value|secret value/i).first().fill("not-a-real-tenant-secret");
    await page.getByRole("button", { name: /^(save|create|add)$/i }).first().click();

    // The tenant-default secret row testid.
    await expect(
      page.getByTestId("tenant-default-secret-row").filter({ hasText: name }),
    ).toBeVisible({ timeout: 10_000 });

    // Delete — locate by row, click delete, confirm.
    const row = page
      .getByTestId("tenant-default-secret-row")
      .filter({ hasText: name });
    await row.getByRole("button", { name: /delete|remove/i }).first().click();
    const confirm = page.getByRole("button", { name: /^(delete|remove|confirm)$/i });
    if (await confirm.first().isVisible().catch(() => false)) {
      await confirm.first().click();
    }
    await expect(row).toHaveCount(0, { timeout: 10_000 });
  });
});
