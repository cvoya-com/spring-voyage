// E2E: Start-engagement affordances on the Units page (#1456).
//
// The /units explorer surfaces:
//   - a tenant-wide "New engagement" header CTA, and
//   - a per-unit / per-agent "Start engagement" button on the
//     unit-pane-actions strip.
//
// Both navigate to /engagement/new — the per-node variant pre-seeds
// the participant via `?participant=`.

import { unitName, agentName } from "../../fixtures/ids.js";
import { apiPost, apiPut } from "../../fixtures/api.js";
import { DEFAULT_MODEL, PROVIDER_ID, TOOL_ID } from "../../fixtures/runtime.js";
import { expect, test } from "../../fixtures/test.js";

test.describe("units page — Start engagement CTAs (#1456)", () => {
  test("the explorer header carries a 'New engagement' button that lands on /engagement/new with no participants", async ({
    page,
  }) => {
    await page.goto("/units");
    await expect(page.getByTestId("units-page-new-engagement")).toBeVisible();
    await page.getByTestId("units-page-new-engagement").click();
    await expect(page).toHaveURL(/\/engagement\/new(\?|$)/);
    await expect(page.getByTestId("engagement-new-form")).toBeVisible();
  });

  test("the unit-pane 'Start engagement' button pre-seeds the unit", async ({
    page,
    tracker,
  }) => {
    const unit = tracker.unit(unitName("eng-cta-unit"));
    await apiPost("/api/v1/tenant/units", {
      name: unit,
      displayName: unit,
      description: "Engagement CTA spec — unit (e2e-portal)",
      tool: TOOL_ID,
      provider: PROVIDER_ID,
      model: DEFAULT_MODEL,
      hosting: "ephemeral",
      isTopLevel: true,
    });

    await page.goto(`/units?node=${encodeURIComponent(unit)}`);
    await expect(
      page.getByTestId("unit-action-start-engagement"),
    ).toBeVisible();
    await page.getByTestId("unit-action-start-engagement").click();

    await expect(page).toHaveURL(
      new RegExp(
        `/engagement/new\\?participant=${encodeURIComponent(`unit://${unit}`)}`,
      ),
    );
    await expect(
      page.getByTestId(`engagement-new-chip-unit-${unit}`),
    ).toBeVisible();
  });

  test("the agent-pane 'Start engagement' button pre-seeds the agent", async ({
    page,
    tracker,
  }) => {
    const unit = tracker.unit(unitName("eng-cta-agent-host"));
    const agent = tracker.agent(agentName("eng-cta-ada"));
    await apiPost("/api/v1/tenant/units", {
      name: unit,
      displayName: unit,
      description: "Engagement CTA spec — agent (e2e-portal)",
      tool: TOOL_ID,
      provider: PROVIDER_ID,
      model: DEFAULT_MODEL,
      hosting: "ephemeral",
      isTopLevel: true,
    });
    await apiPut(
      `/api/v1/tenant/units/${encodeURIComponent(unit)}/execution`,
      { image: "localhost/spring-dapr-agent", runtime: "podman" },
    );
    await apiPost("/api/v1/tenant/agents", {
      name: agent,
      displayName: agent,
      description: "Engagement CTA spec — agent (e2e-portal)",
      unitIds: [unit],
    });

    // Land directly on the agent's explorer node.
    await page.goto(`/units?node=${encodeURIComponent(agent)}`);
    await expect(
      page.getByTestId("agent-action-start-engagement"),
    ).toBeVisible();
    await page.getByTestId("agent-action-start-engagement").click();

    await expect(page).toHaveURL(
      new RegExp(
        `/engagement/new\\?participant=${encodeURIComponent(`agent://${agent}`)}`,
      ),
    );
    await expect(
      page.getByTestId(`engagement-new-chip-agent-${agent}`),
    ).toBeVisible();
  });
});
