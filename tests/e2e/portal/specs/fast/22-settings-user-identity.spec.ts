import { expect, test } from "../../fixtures/test.js";

/**
 * /settings/user-identity surfaces the OSS operator's display
 * identities per connector (ADR-0047 §§ 2, 4). The page is strictly
 * display-only — no PAT input — and the GitHub card renders the
 * schema-driven `username` + `display_handle` form fields.
 *
 * This spec asserts the page renders, the GitHub card is present, and
 * that the form fields the connector's user-config schema declares
 * are visible. We do NOT exercise OAuth here (CI has no live GitHub
 * App / OAuth flow); the connector-card vitest covers the OAuth-
 * callback handoff path against a mock window.
 */

test.describe("settings — user identity page (ADR-0047 §§ 2, 4)", () => {
  test("renders the page and exposes the GitHub card's schema-driven fields", async ({
    page,
  }) => {
    await page.goto("/settings/user-identity");

    await expect(
      page.getByRole("heading", { level: 1, name: /user identity/i }),
    ).toBeVisible({ timeout: 10_000 });

    // GitHub card and its schema-driven fields. The card is rendered
    // when the GitHub connector is installed on the tenant; on CI the
    // dev seed installs every connector.
    await expect(
      page.getByTestId("user-identity-card-github"),
    ).toBeVisible({ timeout: 10_000 });
    await expect(
      page.getByTestId("user-identity-input-github-username"),
    ).toBeVisible();
    await expect(
      page.getByTestId("user-identity-input-github-display_handle"),
    ).toBeVisible();

    // Strictly display-only: no PAT input on this page.
    await expect(
      page.getByLabel(/pat/i, { exact: false }),
    ).toHaveCount(0);
  });

  test("renders the orphan-secret hygiene surface", async ({ page }) => {
    await page.goto("/settings/user-identity");
    await expect(
      page.getByTestId("user-identity-orphan-secrets"),
    ).toBeVisible({ timeout: 10_000 });
  });
});
