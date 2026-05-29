import { expect, test, type Route } from "@playwright/test";

/**
 * Smoke spec for the Slack connector settings panel + install wizard
 * (#2820 / #2882, ADR-0061). Exercises the empty state, the empty-state
 * CTA routing to the one-page install wizard, the inline reconnect OAuth
 * flow (Grid refusal + postMessage success + postMessage Grid error), and
 * the disconnect confirmation modal launched from the bound state. We
 * never reach the real Slack API — every connector call is route-mocked so
 * the test stays deterministic.
 *
 * Note on #2882: the empty-state "Install in Slack workspace" CTA now
 * routes to `/connectors/slack/install` (which registers the app via the
 * Manifest API first). The inline OAuth popup flow survives on the
 * bound-state **Reconnect** button — where the app + credentials already
 * exist — so the OAuth-handoff smokes target Reconnect.
 */

const BOUND_BINDING = {
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
};

async function fulfillNoBinding(route: Route): Promise<void> {
  await route.fulfill({
    status: 404,
    contentType: "application/problem+json",
    body: JSON.stringify({
      title: "Not Found",
      status: 404,
      detail: "No tenant binding exists for connector 'slack'.",
    }),
  });
}

async function fulfillBound(route: Route): Promise<void> {
  await route.fulfill({
    status: 200,
    contentType: "application/json",
    body: JSON.stringify(BOUND_BINDING),
  });
}

test.describe("Slack connector panel", () => {
  test("empty state renders the install CTA", async ({ page }) => {
    await page.route(
      "**/api/v1/tenant/connectors/slack/binding",
      fulfillNoBinding,
    );

    await page.goto("/settings");

    const panel = page.getByTestId("slack-panel-empty");
    await expect(panel).toBeVisible();
    const cta = page.getByTestId("slack-panel-install");
    await expect(cta).toBeVisible();
    await expect(cta).toContainText(/Install in Slack workspace/);
  });

  // #2882: the empty-state CTA routes to the one-page install wizard
  // rather than starting OAuth inline.
  test("empty-state CTA routes to the install wizard", async ({ page }) => {
    await page.route(
      "**/api/v1/tenant/connectors/slack/binding",
      fulfillNoBinding,
    );
    // The wizard checks install status on mount — report not-configured so
    // it renders the registration form (not the connect-now shortcut).
    await page.route(
      "**/api/v1/tenant/connectors/slack/install/status",
      async (route) => {
        await route.fulfill({
          status: 200,
          contentType: "application/json",
          body: JSON.stringify({ oauthConfigured: false }),
        });
      },
    );

    await page.goto("/settings");
    await page.getByTestId("slack-panel-install").click();

    await expect(page).toHaveURL(/\/connectors\/slack\/install$/);
    await expect(page.getByTestId("slack-install-config-token")).toBeVisible();
  });

  // #2882: when credentials already resolve, the wizard offers a one-click
  // connect-now shortcut that skips registration.
  test("wizard surfaces the connect-now shortcut when credentials are configured", async ({
    page,
  }) => {
    await page.route(
      "**/api/v1/tenant/connectors/slack/install/status",
      async (route) => {
        await route.fulfill({
          status: 200,
          contentType: "application/json",
          body: JSON.stringify({ oauthConfigured: true }),
        });
      },
    );

    await page.goto("/connectors/slack/install");

    await expect(
      page.getByTestId("slack-install-connect-existing"),
    ).toBeVisible();
  });

  // ADR-0061 §2.4 — Grid is refused at install time. Reconnect (the
  // surviving inline-OAuth path) surfaces it with the actionable banner
  // and no generic retry.
  test("reconnect Grid refusal renders the actionable banner, not the generic retry", async ({
    page,
  }) => {
    await page.route(
      "**/api/v1/tenant/connectors/slack/binding",
      fulfillBound,
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
    await page.getByTestId("slack-panel-reconnect").click();

    const banner = page.getByTestId("slack-panel-error-enterprise-grid");
    await expect(banner).toBeVisible();
    await expect(banner).toContainText(
      /Slack Enterprise Grid isn't supported in v0\.1/,
    );
    await expect(page.getByTestId("slack-panel-error-retry")).toHaveCount(0);
  });

  // Issue #2837: the OAuth callback fires a postMessage at the popup's
  // opener. Reconnect re-runs OAuth; on the success message the panel
  // toasts and stays bound. The popup loads a same-origin stub that mimics
  // the backend's HTML callback (real targets aren't reachable here, and
  // posting from the parent wouldn't satisfy the strict-source check).
  test("reconnect postMessage success path completes via the popup", async ({
    page,
    context,
  }) => {
    await context.route(
      "**/api/v1/tenant/connectors/slack/binding",
      fulfillBound,
    );
    await context.route(
      "**/api/v1/tenant/connectors/slack/oauth/authorize",
      async (route) => {
        const baseUrl = new URL(route.request().url()).origin;
        await route.fulfill({
          status: 200,
          contentType: "application/json",
          body: JSON.stringify({
            authorizeUrl: `${baseUrl}/__e2e/slack-stub-success`,
            state: "abc",
          }),
        });
      },
    );
    await context.route("**/__e2e/slack-stub-success", async (route) => {
      await route.fulfill({
        status: 200,
        contentType: "text/html",
        body: `<!doctype html>
<html><body>
<script>
  setTimeout(function () {
    if (window.opener && !window.opener.closed) {
      window.opener.postMessage(
        { type: "sv:slack:oauth:done", status: "success" },
        window.location.origin
      );
    }
  }, 100);
</script>
</body></html>`,
      });
    });

    await page.goto("/settings");
    await expect(page.getByTestId("slack-panel-bound")).toBeVisible();
    await page.getByTestId("slack-panel-reconnect").click();

    // The success toast confirms the postMessage handoff completed; the
    // panel stays bound.
    await expect(page.getByText("Slack workspace connected")).toBeVisible();
    await expect(page.getByTestId("slack-panel-bound")).toBeVisible();
  });

  // Issue #2837: a postMessage carrying the Grid error code renders the
  // actionable Grid banner, NOT the generic retry — same as the
  // authorize-error path.
  test("reconnect postMessage error with Grid code renders Grid banner", async ({
    page,
    context,
  }) => {
    await context.route(
      "**/api/v1/tenant/connectors/slack/binding",
      fulfillBound,
    );
    await context.route(
      "**/api/v1/tenant/connectors/slack/oauth/authorize",
      async (route) => {
        const baseUrl = new URL(route.request().url()).origin;
        await route.fulfill({
          status: 200,
          contentType: "application/json",
          body: JSON.stringify({
            authorizeUrl: `${baseUrl}/__e2e/slack-stub-grid`,
            state: "abc",
          }),
        });
      },
    );
    await context.route("**/__e2e/slack-stub-grid", async (route) => {
      await route.fulfill({
        status: 422,
        contentType: "text/html",
        body: `<!doctype html>
<html><body>
<script>
  setTimeout(function () {
    if (window.opener && !window.opener.closed) {
      window.opener.postMessage(
        {
          type: "sv:slack:oauth:done",
          status: "error",
          error: "SlackEnterpriseGridUnsupported",
          message: "Slack Enterprise Grid installs are not supported in v0.1."
        },
        window.location.origin
      );
    }
  }, 100);
</script>
</body></html>`,
      });
    });

    await page.goto("/settings");
    await expect(page.getByTestId("slack-panel-bound")).toBeVisible();
    await page.getByTestId("slack-panel-reconnect").click();

    await expect(
      page.getByTestId("slack-panel-error-enterprise-grid"),
    ).toBeVisible();
    await expect(page.getByTestId("slack-panel-error-retry")).toHaveCount(0);
  });

  test("bound state surfaces workspace facts and the disconnect modal", async ({
    page,
  }) => {
    await page.route(
      "**/api/v1/tenant/connectors/slack/binding",
      fulfillBound,
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
    await expect(dialog).toContainText(/Disconnect Slack workspace\?/);
    // Cancel is the first focusable in the dialog — the focus-trap
    // primitive lands focus on it on open.
    await expect(dialog.getByRole("button", { name: "Cancel" })).toBeFocused();
  });
});
