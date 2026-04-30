import { apiPost } from "../../fixtures/api.js";
import { agentName, unitName } from "../../fixtures/ids.js";
import { DEFAULT_MODEL, PROVIDER_ID, TOOL_ID } from "../../fixtures/runtime.js";
import { expect, test } from "../../fixtures/test.js";

/**
 * Start a new thread from a unit detail page using the "+ New conversation"
 * affordance and confirm it materialises in the engagement portal.
 */

test.describe("threads — start from unit detail", () => {
  test('"+ New conversation" lands on a fresh thread page', async ({
    page,
    tracker,
    ollamaUp,
  }) => {
    void ollamaUp;
    const unit = tracker.unit(unitName("thr-new"));
    const agent = tracker.agent(agentName("thr-new-ada"));

    await apiPost("/api/v1/tenant/units", {
      name: unit,
      displayName: unit,
      description: "New thread spec (e2e-portal)",
      tool: TOOL_ID,
      provider: PROVIDER_ID,
      model: DEFAULT_MODEL,
      hosting: "ephemeral",
      isTopLevel: true,
    });
    await apiPost("/api/v1/tenant/agents", {
      id: agent,
      displayName: agent,
      unitIds: [unit],
    });

    await page.goto(`/units/${unit}`);
    // Find a "New conversation" / "Start conversation" affordance.
    await page
      .getByRole("button", { name: /new conversation|start (conversation|engagement)/i })
      .first()
      .click();

    await expect(page.getByTestId("new-conversation-body")).toBeVisible();
    await page
      .getByTestId("new-conversation-body")
      .getByRole("textbox")
      .first()
      .fill("Status check from e2e-portal.");
    await page.getByTestId("new-conversation-submit").click();

    // Lands on either the engagement detail page or the management portal's
    // thread detail (depending on which surface the affordance routes to).
    await expect(async () => {
      const url = page.url();
      expect(/\/engagement\/|\/threads?\/|\/conversations?\//.test(url), `unexpected URL: ${url}`).toBe(true);
    }).toPass({ timeout: 15_000 });
  });
});
