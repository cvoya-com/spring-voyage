import { expect, test } from "@playwright/test";

/**
 * Smoke spec for the Slack connector settings panel (#2820,
 * ADR-0061). Exercises the three states the brief calls out:
 * empty, Enterprise-Grid refusal, and the disconnect confirmation
 * modal launched from the bound state. We never reach the real
 * Slack API — every connector call is route-mocked on `page.route`
 * so the test stays deterministic.
 */

test.describe("Slack connector panel", () => {
  test("empty state renders the install CTA", async ({ page }) => {
    // 404 on the binding endpoint puts the panel in empty state.
    await page.route(
      "**/api/v1/tenant/connectors/slack/binding",
      async (route) => {
        await route.fulfill({
          status: 404,
          contentType: "application/problem+json",
          body: JSON.stringify({
            title: "Not Found",
            status: 404,
            detail: "No tenant binding exists for connector 'slack'.",
          }),
        });
      },
    );

    await page.goto("/settings");

    // The panel renders its empty-state container with the install CTA.
    const panel = page.getByTestId("slack-panel-empty");
    await expect(panel).toBeVisible();
    const cta = page.getByTestId("slack-panel-install");
    await expect(cta).toBeVisible();
    await expect(cta).toContainText(/Install in Slack workspace/);
  });

  test("Enterprise-Grid error renders the actionable banner, not the generic retry", async ({
    page,
  }) => {
    // Empty binding, but the OAuth authorize endpoint returns the
    // Grid-refusal ProblemDetails.
    await page.route(
      "**/api/v1/tenant/connectors/slack/binding",
      async (route) => {
        await route.fulfill({
          status: 404,
          contentType: "application/problem+json",
          body: JSON.stringify({
            title: "Not Found",
            status: 404,
            detail: "No tenant binding exists.",
          }),
        });
      },
    );
    await page.route(
      "**/api/v1/tenant/connectors/slack/oauth/authorize",
      async (route) => {
        await route.fulfill({
          status: 422,
          contentType: "application/problem+json",
          body: JSON.stringify({
            title: "Slack Enterprise Grid is not supported",
            status: 422,
            detail:
              "ADR-0061 §2.4 — Slack Enterprise Grid bindings are not supported in v0.1.",
            code: "SlackEnterpriseGridUnsupported",
            enterprise_id: "E123",
          }),
        });
      },
    );

    await page.goto("/settings");
    await page.getByTestId("slack-panel-install").click();

    const banner = page.getByTestId("slack-panel-error-enterprise-grid");
    await expect(banner).toBeVisible();
    await expect(banner).toContainText(
      /Slack Enterprise Grid isn't supported in v0\.1/,
    );

    // The "Try again" CTA is reserved for generic errors — Grid
    // refusal has no recoverable retry, so the button must NOT render.
    await expect(page.getByTestId("slack-panel-error-retry")).toHaveCount(0);
  });

  test("bound state surfaces workspace facts and the disconnect modal", async ({
    page,
  }) => {
    await page.route(
      "**/api/v1/tenant/connectors/slack/binding",
      async (route) => {
        await route.fulfill({
          status: 200,
          contentType: "application/json",
          body: JSON.stringify({
            connectorSlug: "slack",
            typeId: "2c8d5b1f-9a4e-4f8b-b7c3-3e1d4a5b6c70",
            boundAt: "2026-05-26T10:00:00Z",
            config: {
              team_id: "T012345",
              team_name: "Acme Engineering",
              bot_user_id: "U_BOT_999",
              bot_token_secret_name: "slack/bot-token",
              signing_secret_secret_name: "slack/signing-secret",
              installer_user_id: "U_OP_111",
              single_user_mode: true,
              mode: "Workspace",
              bound_users: [
                {
                  slack_user_id: "U_OP_111",
                  tenant_user_id: "11111111-2222-3333-4444-555555555555",
                },
              ],
            },
          }),
        });
      },
    );

    await page.goto("/settings");

    const bound = page.getByTestId("slack-panel-bound");
    await expect(bound).toBeVisible();
    await expect(page.getByTestId("slack-panel-bound-workspace")).toHaveText(
      "Acme Engineering",
    );
    await expect(page.getByTestId("slack-panel-bound-team-id")).toHaveText(
      "T012345",
    );
    await expect(page.getByTestId("slack-panel-bound-bot-user")).toHaveText(
      "U_BOT_999",
    );
    await expect(page.getByTestId("slack-panel-bound-installer")).toHaveText(
      "U_OP_111",
    );

    // Open the disconnect confirmation modal.
    await page.getByTestId("slack-panel-disconnect").click();
    const dialog = page.getByRole("dialog");
    await expect(dialog).toBeVisible();
    await expect(dialog).toContainText(
      /Disconnect Slack workspace\?/,
    );
    // Cancel is the first focusable in the dialog — the focus-trap
    // primitive lands focus on it on open.
    await expect(dialog.getByRole("button", { name: "Cancel" })).toBeFocused();
  });
});
