import { seedUnit } from "../../fixtures/api.js";
import { unitName } from "../../fixtures/ids.js";
import { expect, test } from "../../fixtures/test.js";
import { gotoExplorerUnit } from "../../helpers/nav.js";

/**
 * Unit detail page — every tab renders without throwing.
 *
 * The detail page lazy-loads each tab's data; this spec proves none of
 * them blow up against a freshly-created unit (no agents, no secrets,
 * no execution overrides). Boundary / Execution / Secrets are sub-tabs
 * of Config (#2254) — exercise them via deep-links so the spec is robust
 * to layout shuffles. The historical "Agents" tab is now "Members"
 * (#2270 / #2427).
 */

const TOP_LEVEL_TABS = [
  "Overview",
  "Members",
  "Policies",
  "Config",
] as const;

const PANEL_TEST_IDS: Record<(typeof TOP_LEVEL_TABS)[number], string | null> = {
  Overview: null,
  Members: "unit-members-tab",
  Policies: "policies-tab-effective",
  Config: "tab-unit-config",
};

const CONFIG_SUBTABS = [
  { name: "Boundary", panelTestId: "boundary-tab" },
  { name: "Execution", panelTestId: "execution-tab" },
  { name: "Secrets", panelTestId: null }, // panel testid is unit-secret-row-* per row
] as const;

test.describe("units — detail page tabs", () => {
  test("every primary tab renders for a freshly-created unit", async ({
    page,
    tracker,
  }) => {
    const name = tracker.unit(unitName("tabs"));
    const u = await seedUnit(name, {
      description: "Detail tabs spec (e2e-portal)",
    });

    // Deep-link into each top-level tab. Clicking the TabStrip works
    // too, but it depends on the explorer's ?tab= writeback round-trip
    // — going directly removes the race and keeps the spec focused on
    // "does the panel render?".
    for (const label of TOP_LEVEL_TABS) {
      await gotoExplorerUnit(page, u.hex, { tab: label });
      await expect(page.getByTestId("detail-title")).toContainText(name);
      const tid = PANEL_TEST_IDS[label];
      if (tid) {
        await expect(page.getByTestId(tid)).toBeVisible({ timeout: 10_000 });
      }
      await expect(
        page.getByRole("alert").filter({ hasText: /failed|error/i }),
      ).toHaveCount(0, { timeout: 5_000 });
    }

    // Config sub-tabs round-trip via the URL. Hit each one and confirm
    // the panel renders without an alert.
    for (const sub of CONFIG_SUBTABS) {
      await gotoExplorerUnit(page, u.hex, { tab: "Config", subtab: sub.name });
      if (sub.panelTestId) {
        await expect(page.getByTestId(sub.panelTestId)).toBeVisible({
          timeout: 10_000,
        });
      }
      await expect(
        page.getByRole("alert").filter({ hasText: /failed|error/i }),
      ).toHaveCount(0, { timeout: 5_000 });
    }
  });
});
