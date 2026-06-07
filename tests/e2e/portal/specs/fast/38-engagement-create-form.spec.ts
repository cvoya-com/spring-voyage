// E2E: New-engagement form — picker + validation (#1455 / #1456).
//
// Drives the `/engagement/new` page without sending the seed message
// (which would require a working agent dispatcher). The LLM-pool spec
// covers the multi-turn happy path against a live Ollama; here we
// exercise the picker, the seeded-participant query string, and the
// inline validation.
//
// The picker keys every row / chip on the participant's canonical hex id
// (#2473) — `engagement-new-pick-unit-<hex>`, `…-chip-unit-<hex>` — while
// the visible label is the unit's display name. `?participant=unit://<hex>`
// pre-seeds by the same hex.

import { unitName } from "../../fixtures/ids.js";
import { seedUnit } from "../../fixtures/api.js";
import { expect, test } from "../../fixtures/test.js";

test.describe("engagement — new-engagement form (#1455)", () => {
  test("picker lists every Unit and Agent and toggles selection chips", async ({
    page,
    tracker,
  }) => {
    const unit = tracker.unit(unitName("eng-pick"));
    const u = await seedUnit(unit, {
      description: "Engagement picker spec (e2e-portal)",
    });

    await page.goto("/engagement/new");
    await expect(page.getByTestId("engagement-new-page")).toBeVisible();
    await expect(page.getByTestId("engagement-new-form")).toBeVisible();

    // Filter narrows to our unit (by display name) so the picker is robust
    // against the pre-seeded units / agents.
    await page.getByTestId("engagement-new-filter").fill(unit);
    await expect(
      page.getByTestId(`engagement-new-pick-unit-${u.hex}`),
    ).toBeVisible();

    // Toggle on then off.
    await page.getByTestId(`engagement-new-pick-unit-${u.hex}`).click();
    await expect(
      page.getByTestId(`engagement-new-chip-unit-${u.hex}`),
    ).toBeVisible();
    await page
      .getByTestId(`engagement-new-chip-remove-unit-${u.hex}`)
      .click();
    await expect(
      page.getByTestId(`engagement-new-chip-unit-${u.hex}`),
    ).toHaveCount(0);
  });

  test("submitting with no participants surfaces an inline error", async ({
    page,
  }) => {
    await page.goto("/engagement/new");
    await page.getByTestId("engagement-new-body").fill("Hello");
    await page.getByTestId("engagement-new-submit").click();
    await expect(page.getByTestId("engagement-new-error")).toContainText(
      /at least one participant/i,
    );
  });

  test("submitting without an opening message surfaces an inline error", async ({
    page,
    tracker,
  }) => {
    const unit = tracker.unit(unitName("eng-noseed"));
    const u = await seedUnit(unit, {
      description: "Engagement empty-message spec (e2e-portal)",
    });

    await page.goto("/engagement/new");
    await page.getByTestId("engagement-new-filter").fill(unit);
    await page.getByTestId(`engagement-new-pick-unit-${u.hex}`).click();
    await page.getByTestId("engagement-new-submit").click();
    await expect(page.getByTestId("engagement-new-error")).toContainText(
      /first message/i,
    );
  });
});

test.describe("engagement — pre-seeded from query string (#1456)", () => {
  test("`?participant=unit://<id>` lands as a chip", async ({
    page,
    tracker,
  }) => {
    const unit = tracker.unit(unitName("eng-pre"));
    const u = await seedUnit(unit, {
      description: "Engagement pre-seeded spec (e2e-portal)",
    });

    await page.goto(
      "/engagement/new?participant=" +
        encodeURIComponent(`unit://${u.hex}`),
    );
    await expect(
      page.getByTestId(`engagement-new-chip-unit-${u.hex}`),
    ).toBeVisible();
  });

  test("multiple `?participant=` values seed multiple chips", async ({
    page,
    tracker,
  }) => {
    const unitA = tracker.unit(unitName("eng-pre-a"));
    const unitB = tracker.unit(unitName("eng-pre-b"));
    const a = await seedUnit(unitA, { description: "A (e2e-portal)" });
    const b = await seedUnit(unitB, { description: "B (e2e-portal)" });

    await page.goto(
      "/engagement/new?participant=" +
        encodeURIComponent(`unit://${a.hex}`) +
        "&participant=" +
        encodeURIComponent(`unit://${b.hex}`),
    );
    await expect(
      page.getByTestId(`engagement-new-chip-unit-${a.hex}`),
    ).toBeVisible();
    await expect(
      page.getByTestId(`engagement-new-chip-unit-${b.hex}`),
    ).toBeVisible();
  });

  test("a pre-seeded chip is removable before confirm", async ({
    page,
    tracker,
  }) => {
    const unit = tracker.unit(unitName("eng-pre-rm"));
    const u = await seedUnit(unit, {
      description: "Engagement pre-seeded-remove spec (e2e-portal)",
    });

    await page.goto(
      "/engagement/new?participant=" +
        encodeURIComponent(`unit://${u.hex}`),
    );
    await page
      .getByTestId(`engagement-new-chip-remove-unit-${u.hex}`)
      .click();
    await expect(
      page.getByTestId(`engagement-new-chip-unit-${u.hex}`),
    ).toHaveCount(0);
  });
});
