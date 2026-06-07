// E2E: Engagement affordance on the Units page (#1461 / #1462 / #1463 / #1464).
//
// The /units explorer surfaces a single per-node "Engagement" button on
// the unit-pane-actions strip. The legacy top-right "New engagement"
// header CTA was removed (#1461 / #1462) — the page header now only
// hosts "New unit". Clicking the per-node button navigates to
// /engagement/mine with the unit/agent pre-selected via ?unit=<id> /
// ?agent=<id> (#1463), and the label reads "Engagement" (#1464).
//
// The pre-selected id is the node's canonical hex id (#2473).

import { unitName, agentName } from "../../fixtures/ids.js";
import { seedAgent, seedUnit } from "../../fixtures/api.js";
import { expect, test } from "../../fixtures/test.js";
import { gotoExplorerUnit } from "../../helpers/nav.js";

test.describe("units page — Engagement affordance (#1461–#1464)", () => {
  test("the explorer page header no longer carries a 'New engagement' CTA", async ({
    page,
  }) => {
    await page.goto("/units");
    // The per-node Engagement button is the only entry point now.
    await expect(
      page.getByTestId("units-page-new-engagement"),
    ).toHaveCount(0);
    // 'New unit' is still the page-level CTA.
    await expect(page.getByTestId("units-page-new-unit")).toBeVisible();
  });

  test("the unit-pane 'Engagement' button opens /engagement/mine?unit=<id>", async ({
    page,
    tracker,
  }) => {
    const unit = tracker.unit(unitName("eng-cta-unit"));
    const u = await seedUnit(unit, {
      description: "Engagement CTA spec — unit (e2e-portal)",
    });

    await gotoExplorerUnit(page, u.hex);
    const button = page.getByTestId("unit-action-engagement");
    await expect(button).toBeVisible();
    await expect(button).toHaveText(/^\s*Engagement\s*$/);
    await button.click();

    await expect(page).toHaveURL(
      new RegExp(`/engagement/mine\\?unit=${u.hex}`),
    );
    await expect(page.getByTestId("my-engagements-page")).toBeVisible();
  });

  test("the agent-pane 'Engagement' button opens /engagement/mine?agent=<id>", async ({
    page,
    tracker,
  }) => {
    const unit = tracker.unit(unitName("eng-cta-agent-host"));
    const agent = tracker.agent(agentName("eng-cta-ada"));
    const u = await seedUnit(unit, {
      description: "Engagement CTA spec — agent (e2e-portal)",
    });
    const a = await seedAgent(agent, {
      description: "Engagement CTA spec — agent (e2e-portal)",
      unitHexIds: [u.hex],
    });

    // Land directly on the agent's explorer node.
    await gotoExplorerUnit(page, a.hex);
    const button = page.getByTestId("agent-action-engagement");
    await expect(button).toBeVisible();
    await expect(button).toHaveText(/^\s*Engagement\s*$/);
    await button.click();

    await expect(page).toHaveURL(
      new RegExp(`/engagement/mine\\?agent=${a.hex}`),
    );
    await expect(page.getByTestId("my-engagements-page")).toBeVisible();
  });
});
