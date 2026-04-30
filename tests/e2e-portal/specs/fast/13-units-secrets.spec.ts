import { apiGet, apiPost } from "../../fixtures/api.js";
import { secretName, unitName } from "../../fixtures/ids.js";
import { DEFAULT_MODEL, PROVIDER_ID, TOOL_ID } from "../../fixtures/runtime.js";
import { expect, test } from "../../fixtures/test.js";

/**
 * Unit secrets — create/list/delete via the Secrets tab.
 *
 * Mirrors `tests/e2e/scenarios/fast/21-secret-cli.sh` (unit-scope branch).
 */

interface UnitSecretListItem {
  name: string;
}

test.describe("units — secrets tab", () => {
  test("create + list + delete a unit-scoped secret", async ({
    page,
    tracker,
  }) => {
    const name = tracker.unit(unitName("secrets"));
    const sName = secretName("u1");

    await apiPost("/api/v1/tenant/units", {
      name,
      displayName: name,
      description: "Secrets spec (e2e-portal)",
      tool: TOOL_ID,
      provider: PROVIDER_ID,
      model: DEFAULT_MODEL,
      hosting: "ephemeral",
      isTopLevel: true,
    });

    await page.goto(`/units/${name}`);
    await page.getByRole("tab", { name: /^secrets$/i }).click();

    // The Secrets tab exposes a "Add secret" affordance.
    await page.getByRole("button", { name: /^(add secret|new secret|create secret)$/i }).first().click();
    await page.getByLabel(/secret name|name/i).first().fill(sName);
    await page.getByLabel(/value|secret value/i).first().fill("not-a-real-secret");
    await page.getByRole("button", { name: /^(save|create|add)$/i }).first().click();

    // The new row should render with the secret-row testid.
    await expect(page.getByTestId(`unit-secret-row-${sName}`)).toBeVisible({ timeout: 10_000 });

    // Cross-check via API (NEVER returns plaintext, but does return the metadata row).
    const secrets = await apiGet<UnitSecretListItem[]>(
      `/api/v1/tenant/units/${encodeURIComponent(name)}/secrets`,
    );
    expect(secrets.find((s) => s.name === sName)).toBeDefined();

    // Delete via UI.
    await page
      .getByTestId(`unit-secret-row-${sName}`)
      .getByRole("button", { name: /delete|remove/i })
      .first()
      .click();
    const confirm = page.getByRole("button", { name: /^(delete|confirm|remove)$/i });
    if (await confirm.first().isVisible().catch(() => false)) {
      await confirm.first().click();
    }
    await expect(page.getByTestId(`unit-secret-row-${sName}`)).toHaveCount(0, { timeout: 10_000 });
  });
});
