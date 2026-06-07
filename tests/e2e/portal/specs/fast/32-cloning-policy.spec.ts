import { seedAgent, seedUnit } from "../../fixtures/api.js";
import { agentName, unitName } from "../../fixtures/ids.js";
import { expect, test } from "../../fixtures/test.js";
import { gotoExplorerUnit } from "../../helpers/nav.js";

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

    const u = await seedUnit(unit, {
      description: "Cloning policy spec (e2e-portal)",
    });
    const a = await seedAgent(agent, {
      description: "Cloning policy spec (e2e-portal)",
      unitHexIds: [u.hex],
    });

    // Agent cloning policy panel renders inside the Policies tab.
    await gotoExplorerUnit(page, a.hex, { tab: "Policies" });
    await expect(page.getByTestId("agent-cloning-policy-panel")).toBeVisible({
      timeout: 10_000,
    });
  });
});
