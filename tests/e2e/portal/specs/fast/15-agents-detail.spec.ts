import { seedAgent, seedUnit } from "../../fixtures/api.js";
import { agentName, unitName } from "../../fixtures/ids.js";
import { expect, test } from "../../fixtures/test.js";
import { gotoExplorerUnit } from "../../helpers/nav.js";

/**
 * Agent detail page — every primary panel renders without throwing.
 *
 * Agent detail lives inside the Explorer (`/explorer/units/<agentHex>`).
 * The panels are split across tabs (#2254): Execution + Budget under
 * Config (`?subtab=…`), Initiative + Cloning under Policies.
 */

test.describe("agents — detail page panels", () => {
  test("overview / execution / budget / policies (initiative + cloning) panels render", async ({
    page,
    tracker,
  }) => {
    const unit = tracker.unit(unitName("ad-host"));
    const ada = tracker.agent(agentName("ada-detail"));

    const u = await seedUnit(unit, {
      description: "Agent detail spec (e2e-portal)",
    });
    const a = await seedAgent(ada, {
      description: "Agent detail spec (e2e-portal)",
      unitHexIds: [u.hex],
    });

    // Overview — the detail title carries the agent's displayName.
    await gotoExplorerUnit(page, a.hex, { tab: "Overview" });
    await expect(page.getByTestId("detail-title")).toContainText(ada);

    // Execution panel (Config → Execution).
    await gotoExplorerUnit(page, a.hex, { tab: "Config", subtab: "Execution" });
    await expect(page.getByTestId("agent-execution-panel")).toBeVisible({
      timeout: 10_000,
    });

    // Budget panel (Config → Budget).
    await gotoExplorerUnit(page, a.hex, { tab: "Config", subtab: "Budget" });
    await expect(page.getByTestId("agent-budget-panel")).toBeVisible({
      timeout: 10_000,
    });

    // Policies tab stacks Initiative + Cloning for agents.
    await gotoExplorerUnit(page, a.hex, { tab: "Policies" });
    await expect(page.getByTestId("agent-initiative-panel")).toBeVisible({
      timeout: 10_000,
    });
    await expect(page.getByTestId("agent-cloning-policy-panel")).toBeVisible();

    // No tab should have surfaced a load error.
    await expect(
      page.getByRole("alert").filter({ hasText: /failed|error/i }),
    ).toHaveCount(0, { timeout: 5_000 });
  });
});
