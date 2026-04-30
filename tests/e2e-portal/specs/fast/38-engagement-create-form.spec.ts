// E2E: New-engagement form — picker + validation (#1455 / #1456).
//
// Drives the `/engagement/new` page without sending the seed message
// (which would require a working agent dispatcher). The LLM-pool spec
// covers the multi-turn happy path against a live Ollama; here we
// exercise the picker, the seeded-participant query string, and the
// inline validation.

import { unitName } from "../../fixtures/ids.js";
import { apiPost } from "../../fixtures/api.js";
import { DEFAULT_MODEL, PROVIDER_ID, TOOL_ID } from "../../fixtures/runtime.js";
import { expect, test } from "../../fixtures/test.js";

test.describe("engagement — new-engagement form (#1455)", () => {
  test("picker lists every Unit and Agent and toggles selection chips", async ({
    page,
    tracker,
  }) => {
    const unit = tracker.unit(unitName("eng-pick"));
    await apiPost("/api/v1/tenant/units", {
      name: unit,
      displayName: unit,
      description: "Engagement picker spec (e2e-portal)",
      tool: TOOL_ID,
      provider: PROVIDER_ID,
      model: DEFAULT_MODEL,
      hosting: "ephemeral",
      isTopLevel: true,
    });

    await page.goto("/engagement/new");
    await expect(page.getByTestId("engagement-new-page")).toBeVisible();
    await expect(page.getByTestId("engagement-new-form")).toBeVisible();

    // Filter narrows to our unit so the picker is robust against the
    // pre-seeded `sv-test` units / agents.
    await page.getByTestId("engagement-new-filter").fill(unit);
    await expect(
      page.getByTestId(`engagement-new-pick-unit-${unit}`),
    ).toBeVisible();

    // Toggle on then off.
    await page.getByTestId(`engagement-new-pick-unit-${unit}`).click();
    await expect(
      page.getByTestId(`engagement-new-chip-unit-${unit}`),
    ).toBeVisible();
    await page
      .getByTestId(`engagement-new-chip-remove-unit-${unit}`)
      .click();
    await expect(
      page.getByTestId(`engagement-new-chip-unit-${unit}`),
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
    await apiPost("/api/v1/tenant/units", {
      name: unit,
      displayName: unit,
      description: "Engagement empty-message spec (e2e-portal)",
      tool: TOOL_ID,
      provider: PROVIDER_ID,
      model: DEFAULT_MODEL,
      hosting: "ephemeral",
      isTopLevel: true,
    });

    await page.goto("/engagement/new");
    await page.getByTestId("engagement-new-filter").fill(unit);
    await page.getByTestId(`engagement-new-pick-unit-${unit}`).click();
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
    await apiPost("/api/v1/tenant/units", {
      name: unit,
      displayName: unit,
      description: "Engagement pre-seeded spec (e2e-portal)",
      tool: TOOL_ID,
      provider: PROVIDER_ID,
      model: DEFAULT_MODEL,
      hosting: "ephemeral",
      isTopLevel: true,
    });

    await page.goto(
      "/engagement/new?participant=" +
        encodeURIComponent(`unit://${unit}`),
    );
    await expect(
      page.getByTestId(`engagement-new-chip-unit-${unit}`),
    ).toBeVisible();
  });

  test("multiple `?participant=` values seed multiple chips", async ({
    page,
    tracker,
  }) => {
    const unitA = tracker.unit(unitName("eng-pre-a"));
    const unitB = tracker.unit(unitName("eng-pre-b"));
    await apiPost("/api/v1/tenant/units", {
      name: unitA,
      displayName: unitA,
      description: "A (e2e-portal)",
      tool: TOOL_ID,
      provider: PROVIDER_ID,
      model: DEFAULT_MODEL,
      hosting: "ephemeral",
      isTopLevel: true,
    });
    await apiPost("/api/v1/tenant/units", {
      name: unitB,
      displayName: unitB,
      description: "B (e2e-portal)",
      tool: TOOL_ID,
      provider: PROVIDER_ID,
      model: DEFAULT_MODEL,
      hosting: "ephemeral",
      isTopLevel: true,
    });

    await page.goto(
      "/engagement/new?participant=" +
        encodeURIComponent(`unit://${unitA}`) +
        "&participant=" +
        encodeURIComponent(`unit://${unitB}`),
    );
    await expect(
      page.getByTestId(`engagement-new-chip-unit-${unitA}`),
    ).toBeVisible();
    await expect(
      page.getByTestId(`engagement-new-chip-unit-${unitB}`),
    ).toBeVisible();
  });

  test("a pre-seeded chip is removable before confirm", async ({
    page,
    tracker,
  }) => {
    const unit = tracker.unit(unitName("eng-pre-rm"));
    await apiPost("/api/v1/tenant/units", {
      name: unit,
      displayName: unit,
      description: "Engagement pre-seeded-remove spec (e2e-portal)",
      tool: TOOL_ID,
      provider: PROVIDER_ID,
      model: DEFAULT_MODEL,
      hosting: "ephemeral",
      isTopLevel: true,
    });

    await page.goto(
      "/engagement/new?participant=" +
        encodeURIComponent(`unit://${unit}`),
    );
    await page
      .getByTestId(`engagement-new-chip-remove-unit-${unit}`)
      .click();
    await expect(
      page.getByTestId(`engagement-new-chip-unit-${unit}`),
    ).toHaveCount(0);
  });
});
