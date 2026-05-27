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

  // Issue #2837: the OAuth callback now fires a postMessage at the
  // popup's opener. The portal listens for `sv:slack:oauth:done` and
  // refetches the binding on success. We intercept the authorize URL
  // to return a same-origin stub page that posts the success message
  // immediately, simulating the backend's HTML callback in a stable
  // way (real network targets aren't reachable from this smoke
  // suite, and posting from the parent window wouldn't satisfy the
  // listener's strict-source check). Confirms the polling path is
  // gone — the panel reaches the bound state on postMessage alone.
  test("postMessage handoff flips empty → bound on success", async ({
    page,
    context,
  }) => {
    let bound = false;
    // Use context.route — page.route only intercepts the main page's
    // requests; child popups go through context-level interception.
    // The OAuth flow opens a popup, so the stub HTML page must be
    // served via context.
    await context.route(
      "**/api/v1/tenant/connectors/slack/binding",
      async (route) => {
        if (!bound) {
          await route.fulfill({
            status: 404,
            contentType: "application/problem+json",
            body: JSON.stringify({
              title: "Not Found",
              status: 404,
              detail: "No tenant binding exists.",
            }),
          });
          return;
        }
        await route.fulfill({
          status: 200,
          contentType: "application/json",
          body: JSON.stringify({
            connectorSlug: "slack",
            typeId: "2c8d5b1f-9a4e-4f8b-b7c3-3e1d4a5b6c70",
            boundAt: "2026-05-26T10:00:00Z",
            config: {
              team_id: "T-pm",
              team_name: "PM Workspace",
              bot_user_id: "U_BOT_PM",
              bot_token_secret_name: "x",
              signing_secret_secret_name: "y",
              installer_user_id: "U_OP_PM",
              single_user_mode: true,
              mode: "Workspace",
              bound_users: [
                {
                  slack_user_id: "U_OP_PM",
                  tenant_user_id: "22222222-3333-4444-5555-666666666666",
                },
              ],
            },
          }),
        });
      },
    );
    // Point the authorize URL at a same-origin stub that mimics the
    // real backend callback's HTML postMessage page. Loading it in
    // the popup matches what production does — the popup navigates
    // to an HTML page, that page fires postMessage back to opener.
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
  // Small delay so the parent's awaitSlackOAuthHandoff listener has
  // a chance to attach. In production the backend's HTML callback
  // takes longer than this just doing the token exchange, so the
  // race never surfaces; the e2e stub returns instantly so we
  // explicitly defer.
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
    // The initial binding query must see "not bound" so the panel
    // renders the install CTA; we wait for that before flipping the
    // stub to bound.
    await expect(page.getByTestId("slack-panel-install")).toBeVisible();
    bound = true;
    await page.getByTestId("slack-panel-install").click();

    await expect(page.getByTestId("slack-panel-bound")).toBeVisible();
    await expect(page.getByTestId("slack-panel-bound-workspace")).toHaveText(
      "PM Workspace",
    );
  });

  // Issue #2837: a postMessage carrying the Grid error code must
  // render the actionable Grid banner, NOT the generic retry — same
  // as the authorize-error path. The user has to disconnect off-
  // portal before retrying.
  test("postMessage error with Grid code renders Grid banner", async ({
    page,
    context,
  }) => {
    await context.route(
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
    await page.getByTestId("slack-panel-install").click();

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
