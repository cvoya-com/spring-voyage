import { apiGet, seedAgent, seedUnit } from "../../fixtures/api.js";
import { agentName, unitName } from "../../fixtures/ids.js";
import { expect, test } from "../../fixtures/test.js";
import { gotoExplorerUnit } from "../../helpers/nav.js";

/**
 * Per-agent budget set + roundtrip.
 *
 * The Budget panel lives under the agent's Config tab
 * (`?tab=Config&subtab=Budget`) with testid `agent-budget-panel`.
 */

interface BudgetResponse {
  // GET /api/v1/tenant/agents/{id}/budget returns BudgetResponse with
  // a single `dailyBudget` decimal. 404 means "no envelope set".
  dailyBudget?: number;
}

test.describe("agents — budget panel", () => {
  test("set a budget, save, reload and see the persisted amount", async ({
    page,
    tracker,
  }) => {
    const unit = tracker.unit(unitName("bud-host"));
    const ada = tracker.agent(agentName("bud-ada"));

    const u = await seedUnit(unit, { description: "Budget spec (e2e-portal)" });
    const a = await seedAgent(ada, {
      description: "Budget spec (e2e-portal)",
      unitHexIds: [u.hex],
    });

    // The agent node lives inside the explorer; deep-link to its Config →
    // Budget sub-tab. The explorer routes agents under `/explorer/units/<id>`
    // too (the `[id]` segment resolves the tenant-tree node regardless of
    // kind).
    await gotoExplorerUnit(page, a.hex, { tab: "Config", subtab: "Budget" });
    await expect(page.getByTestId("agent-budget-panel")).toBeVisible({ timeout: 10_000 });

    await page.getByTestId("agent-budget-input").fill("12.5");
    await page.getByTestId("agent-budget-save").click();

    await expect
      .poll(
        async () => {
          const budget = await apiGet<BudgetResponse>(
            `/api/v1/tenant/agents/${encodeURIComponent(a.hex)}/budget`,
            { expect: [200, 404] },
          );
          return budget?.dailyBudget ?? null;
        },
        { timeout: 10_000 },
      )
      .toBe(12.5);

    // Reload — UI shows the persisted value.
    await page.reload();
    await expect(page.getByTestId("agent-budget-current")).toContainText(/12\.5/);
  });
});
