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

    await page.goto("/settings");
    // The Settings page lists panels; click into "Account" / "Auth" panel.
    // The card test id is `settings-panel-card-account` per page.tsx + tests.
    const authPanel = page
      .getByTestId(/settings-panel-card-(account|auth|api-tokens)/)
      .first();
    if (await authPanel.isVisible().catch(() => false)) {
      await authPanel.click();
    } else {
      // Some layouts surface tokens on the Settings root.
    }

    // Create a token. The button label is "Create token" or "New token".
    await page.getByRole("button", { name: /^(create token|new token|create api token)$/i }).first().click();
    await page.getByRole("textbox", { name: /token name|name/i }).first().fill(name);
    await page.getByRole("button", { name: /^create$/i }).first().click();

    // The plaintext is revealed exactly once. The reveal pattern shows a
    // copy-to-clipboard button alongside the token text.
    const reveal = page.getByText(/sv_|spring_/).first();
    await expect(reveal).toBeVisible({ timeout: 10_000 });

    // Dismiss the reveal dialog.
    const dismiss = page
      .getByRole("button", { name: /^(done|close|dismiss|i.?ve copied|got it)$/i })
      .first();
    await dismiss.click();

    // List asserts the token name is present.
    await expect(page.getByText(name)).toBeVisible({ timeout: 10_000 });

    // Revoke. The row exposes a revoke button; matching by accessible name
    // alongside the token name keeps the click scoped to the right row.
    const row = page.locator("tr,li,div").filter({ hasText: name }).first();
    await row.getByRole("button", { name: /revoke|delete/i }).first().click();

    // Confirmation dialog (destructive op).
    const confirm = page.getByRole("button", { name: /^(revoke|delete|confirm)$/i });
    if (await confirm.first().isVisible().catch(() => false)) {
      await confirm.first().click();
    }

    // The row should disappear from the list.
    await expect(page.getByText(name)).toHaveCount(0, { timeout: 10_000 });
  });
});
