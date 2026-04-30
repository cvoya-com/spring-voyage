import { apiPost } from "../../fixtures/api.js";
import { agentName, unitName } from "../../fixtures/ids.js";
import { DEFAULT_MODEL, PROVIDER_ID, TOOL_ID } from "../../fixtures/runtime.js";
import { expect, test } from "../../fixtures/test.js";

/**
 * Cloning policy — tenant + per-agent.
 *
 * The Settings page exposes a cloning-policy card. Per-agent edits live
 * on the agent detail page (testid `agent-cloning-policy-panel`).
 */

test.describe("cloning policy", () => {
  test("settings card renders the cloning policy summary", async ({ page }) => {
    await page.goto("/settings");
    await expect(page.getByTestId("settings-cloning-policy-card")).toBeVisible({
      timeout: 10_000,
    });
  });

  test("agent cloning policy panel renders for an existing agent", async ({
    page,
    tracker,
  }) => {
    const unit = tracker.unit(unitName("clone-host"));
    const agent = tracker.agent(agentName("clone-ada"));

    await apiPost("/api/v1/tenant/units", {
      name: unit,
      displayName: unit,
      description: "Cloning policy spec (e2e-portal)",
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

    await page.goto(`/agents/${agent}`);
    // The panel may live behind a tab.
    const tab = page.getByRole("tab", { name: /^cloning|clones$/i });
    if (await tab.first().isVisible().catch(() => false)) {
      await tab.first().click();
    }
    await expect(page.getByTestId("agent-cloning-policy-panel")).toBeVisible({
      timeout: 10_000,
    });
  });
});
