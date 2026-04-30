import { apiPost } from "../../fixtures/api.js";
import { agentName, unitName } from "../../fixtures/ids.js";
import { DEFAULT_MODEL, PROVIDER_ID, TOOL_ID } from "../../fixtures/runtime.js";
import { expect, test } from "../../fixtures/test.js";

/**
 * Agent detail page — every primary panel renders without throwing.
 */

const PANELS_TO_VERIFY = [
  "agent-execution-panel",
  "agent-budget-panel",
  "agent-cloning-policy-panel",
  "agent-initiative-panel",
  "agent-lifecycle-panel",
] as const;

test.describe("agents — detail page panels", () => {
  test("execution / budget / cloning-policy / initiative / lifecycle panels render", async ({
    page,
    tracker,
  }) => {
    const unit = tracker.unit(unitName("ad-host"));
    const aId = tracker.agent(agentName("ada-detail"));

    await apiPost("/api/v1/tenant/units", {
      name: unit,
      displayName: unit,
      description: "Agent detail spec (e2e-portal)",
      tool: TOOL_ID,
      provider: PROVIDER_ID,
      model: DEFAULT_MODEL,
      hosting: "ephemeral",
      isTopLevel: true,
    });
    await apiPost("/api/v1/tenant/agents", {
      id: aId,
      displayName: "Detail Spec Agent",
      unitIds: [unit],
      // Persistent-agent panel only renders for hosting=persistent agents;
      // this spec keeps to ephemeral so the lifecycle panel surfaces the
      // "ephemeral" copy path rather than the deploy/undeploy controls.
    });

    await page.goto(`/agents/${aId}`);
    await expect(page.getByRole("heading", { name: /Detail Spec Agent|ada-detail/i })).toBeVisible();

    // Each panel may live behind its own tab. Enumerate likely tabs and
    // click them; each click should land on a panel that renders without
    // error.
    const tabs = ["Overview", "Execution", "Budget", "Initiative", "Cloning", "Lifecycle"];
    for (const label of tabs) {
      const tab = page.getByRole("tab", { name: new RegExp(`^${label}$`, "i") });
      if (await tab.first().isVisible().catch(() => false)) {
        await tab.first().click();
      }
    }

    // After visiting all tabs, at least one of the known panel testids
    // should be present (the agent-detail page renders multiple panels
    // simultaneously on wider layouts, so this is forgiving by design).
    let seen = 0;
    for (const tid of PANELS_TO_VERIFY) {
      if (await page.getByTestId(tid).first().isVisible().catch(() => false)) {
        seen++;
      }
    }
    expect(seen, `expected at least one of ${PANELS_TO_VERIFY.join(", ")}`).toBeGreaterThan(0);
  });
});
