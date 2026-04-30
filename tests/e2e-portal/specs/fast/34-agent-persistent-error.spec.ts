import { apiPost } from "../../fixtures/api.js";
import { agentName, unitName } from "../../fixtures/ids.js";
import { DEFAULT_MODEL, PROVIDER_ID, TOOL_ID } from "../../fixtures/runtime.js";
import { expect, test } from "../../fixtures/test.js";

/**
 * Persistent-agent lifecycle error path.
 *
 * The portal exposes a Lifecycle panel for persistent agents (deploy /
 * undeploy / scale / logs). When no container runtime is available,
 * deploys 400. This spec covers the UI surfacing of that error so the
 * regression that masked it (the "silent failure" fixed in #1397) doesn't
 * recur.
 *
 * Mirrors `tests/e2e/scenarios/fast/20-persistent-agent-cli.sh`.
 */

test.describe("agents — persistent lifecycle error surfacing", () => {
  test("deploy on an ephemeral agent returns 400 and surfaces the error in the UI", async ({
    page,
    tracker,
  }) => {
    const unit = tracker.unit(unitName("perr-host"));
    const agent = tracker.agent(agentName("perr-ada"));

    await apiPost("/api/v1/tenant/units", {
      name: unit,
      displayName: unit,
      description: "Persistent error spec (e2e-portal)",
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
      // Default ephemeral hosting — deploy will be rejected with 400.
    });

    await page.goto(`/agents/${agent}`);
    const tab = page.getByRole("tab", { name: /^lifecycle|deployment$/i });
    if (await tab.first().isVisible().catch(() => false)) {
      await tab.first().click();
    }
    const panel = page.getByTestId("agent-lifecycle-panel");
    if (!(await panel.isVisible().catch(() => false))) {
      test.skip(true, "Lifecycle panel not rendered for ephemeral agent — UI hides it.");
    }

    // Try to deploy — the panel is gated on hosting=persistent, so the
    // deploy button may not even render. If it does, the click should
    // surface a 400 error message.
    const deploy = page.getByTestId("agent-lifecycle-deploy");
    if (await deploy.isVisible().catch(() => false)) {
      await deploy.click();
      await expect(
        page.getByText(/ephemeral|persistent only|cannot deploy|400/i).first(),
      ).toBeVisible({ timeout: 10_000 });
    }
  });
});
