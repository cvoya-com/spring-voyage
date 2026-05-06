import { apiGet, apiPost } from "../../fixtures/api.js";
import { secretName, unitName } from "../../fixtures/ids.js";
import { AGENT_ID, DEFAULT_MODEL, PROVIDER_ID } from "../../fixtures/runtime.js";
import { expect, test } from "../../fixtures/test.js";

/**
 * Unit secrets — create/list/delete via the Secrets tab.
 *
 * Mirrors `tests/e2e/scenarios/fast/21-secret-cli.sh` (unit-scope branch).
 */

interface UnitSecretListItem {
  name: string;
}
interface UnitSecretListResponse {
  secrets: UnitSecretListItem[];
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
      agent: AGENT_ID,
      provider: PROVIDER_ID,
      model: DEFAULT_MODEL,
      hosting: "ephemeral",
      isTopLevel: true,
    });

    // Secrets moved under Config (subtab) per QUALITY-unit-config-subtabs.
    await page.goto(
      `/units?node=${encodeURIComponent(name)}&tab=Config&subtab=Secrets`,
    );

    // The Add-secret form is rendered inline (no toggle button); just fill
    // it and submit. The form lives inside a Card whose CardTitle is
    // "Add secret" — we can scope to that section by hosting it in the
    // overall Config panel and clicking the submit button labelled
    // "Add secret".
    await page.getByLabel(/^name$/i).fill(sName);
    await page.getByLabel(/^value/i).fill("not-a-real-secret");
    await page.getByRole("button", { name: /^Add secret$/i }).click();

    // The new row should render with the secret-row testid.
    await expect(page.getByTestId(`unit-secret-row-${sName}`)).toBeVisible({ timeout: 10_000 });

    // Cross-check via API (NEVER returns plaintext, but does return the metadata row).
    const response = await apiGet<UnitSecretListResponse>(
      `/api/v1/tenant/units/${encodeURIComponent(name)}/secrets`,
    );
    expect(response.secrets.find((s) => s.name === sName)).toBeDefined();

    // Delete via UI — the row's delete button is aria-labelled
    // `Delete <name>` and there is no confirmation dialog.
    await page
      .getByRole("button", { name: new RegExp(`Delete ${sName}`, "i") })
      .click();
    await expect(page.getByTestId(`unit-secret-row-${sName}`)).toHaveCount(0, { timeout: 10_000 });
  });
});
