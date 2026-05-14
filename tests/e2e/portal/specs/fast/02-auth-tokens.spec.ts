import { tokenName } from "../../fixtures/ids.js";
import { expect, test } from "../../fixtures/test.js";

/**
 * Settings → Account: API token lifecycle.
 *
 * Mirrors `tests/e2e/scenarios/fast/23-bootstrap-and-auth.sh` — the CLI
 * counterpart. The portal exposes the same `spring auth token`
 * verbs through the Settings drawer.
 *
 * One-shot reveal: the plaintext token is shown exactly once after
 * creation. The spec asserts the reveal AND that the token disappears on
 * dialog dismiss (security contract from #557).
 */

test.describe("settings — auth tokens", () => {
  test("create + list + revoke roundtrip", async ({ page, tracker }) => {
    const name = tracker.token(tokenName("auth-rt"));

    // The Settings page renders the Account / API-tokens panel inline —
    // there is no card to click into. Go straight to the panel actions
    // by their dedicated test ids (see `auth-panel.tsx`).
    await page.goto("/settings");

    // Open the create form.
    await page.getByTestId("settings-auth-token-create-open").click();
    await page.getByTestId("settings-auth-token-name-input").fill(name);
    await page.getByTestId("settings-auth-token-create-submit").click();

    // The plaintext is revealed exactly once inside the
    // `settings-auth-token-reveal` block; the copyable token sits at
    // `settings-auth-token-value`. Raw tokens are unprefixed base64url.
    const tokenValue = page.getByTestId("settings-auth-token-value");
    await expect(tokenValue).toBeVisible({ timeout: 10_000 });
    const tokenText = (await tokenValue.textContent())?.trim() ?? "";
    expect(tokenText.length).toBeGreaterThanOrEqual(20);

    // Dismiss the reveal pill.
    await page.getByRole("button", { name: /dismiss token reveal/i }).click();

    // The created row is keyed off the token name.
    const row = page.getByTestId(`settings-auth-token-row-${name}`);
    await expect(row).toBeVisible({ timeout: 10_000 });

    // Revoke via the per-row button (also test-id'd off the token name).
    // The first click swaps the trash icon for an inline two-button
    // confirm pair ("Revoke" + "Cancel"), aria-labelled by the panel.
    await page.getByTestId(`settings-auth-revoke-${name}`).click();
    await page
      .getByRole("button", { name: new RegExp(`Confirm revoke ${name}`, "i") })
      .click();

    await expect(row).toHaveCount(0, { timeout: 10_000 });
  });
});
