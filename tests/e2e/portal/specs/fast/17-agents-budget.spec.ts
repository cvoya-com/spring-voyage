import { apiGet, apiPost } from "../../fixtures/api.js";
import { agentName, unitName } from "../../fixtures/ids.js";
import { AGENT_ID, DEFAULT_MODEL, PROVIDER_ID } from "../../fixtures/runtime.js";
import { expect, test } from "../../fixtures/test.js";

/**
 * Per-agent budget set + roundtrip.
 *
 * The Budget panel is at /agents/<id> with testid `agent-budget-panel`.
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
    const aId = tracker.agent(agentName("bud-ada"));

    await apiPost("/api/v1/tenant/units", {
      name: unit,
      displayName: unit,
      description: "Budget spec (e2e-portal)",
      agent: AGENT_ID,
      provider: PROVIDER_ID,
      model: DEFAULT_MODEL,
      hosting: "ephemeral",
      isTopLevel: true,
    });
    await apiPost("/api/v1/tenant/agents", {
      name: aId,
      displayName: aId,
      description: "Budget spec (e2e-portal)",
      unitIds: [unit],
    });

    // Agent Config tab stacks Execution + Budget + Expertise panels.
    await page.goto(
      `/units?node=${encodeURIComponent(aId)}&tab=Config`,
    );
    await expect(page.getByTestId("agent-budget-panel")).toBeVisible({ timeout: 10_000 });

    await page.getByTestId("agent-budget-input").fill("12.5");
    await page.getByTestId("agent-budget-save").click();

    await expect
      .poll(
        async () => {
          const budget = await apiGet<BudgetResponse>(
            `/api/v1/tenant/agents/${encodeURIComponent(aId)}/budget`,
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
