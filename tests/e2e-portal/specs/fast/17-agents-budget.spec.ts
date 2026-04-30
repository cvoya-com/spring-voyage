import { apiGet, apiPost } from "../../fixtures/api.js";
import { agentName, unitName } from "../../fixtures/ids.js";
import { DEFAULT_MODEL, PROVIDER_ID, TOOL_ID } from "../../fixtures/runtime.js";
import { expect, test } from "../../fixtures/test.js";

/**
 * Per-agent budget set + roundtrip.
 *
 * The Budget panel is at /agents/<id> with testid `agent-budget-panel`.
 */

interface BudgetResponse {
  cap?: { amount?: number; currency?: string } | number | null;
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
      tool: TOOL_ID,
      provider: PROVIDER_ID,
      model: DEFAULT_MODEL,
      hosting: "ephemeral",
      isTopLevel: true,
    });
    await apiPost("/api/v1/tenant/agents", {
      id: aId,
      displayName: aId,
      unitIds: [unit],
    });

    await page.goto(`/agents/${aId}`);
    await expect(page.getByTestId("agent-budget-panel")).toBeVisible({ timeout: 10_000 });

    await page.getByTestId("agent-budget-input").fill("12.5");
    await page.getByTestId("agent-budget-save").click();

    await expect
      .poll(
        async () => {
          const budget = await apiGet<BudgetResponse>(
            `/api/v1/tenant/agents/${encodeURIComponent(aId)}/budget`,
          );
          if (typeof budget.cap === "number") return budget.cap;
          return budget.cap?.amount ?? null;
        },
        { timeout: 10_000 },
      )
      .toBe(12.5);

    // Reload — UI shows the persisted value.
    await page.reload();
    await expect(page.getByTestId("agent-budget-current")).toContainText(/12\.5/);
  });
});
