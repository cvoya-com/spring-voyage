import { apiGet, apiPost } from "../../fixtures/api.js";
import { unitName } from "../../fixtures/ids.js";
import { DEFAULT_MODEL, PROVIDER_ID, TOOL_ID } from "../../fixtures/runtime.js";
import { expect, test } from "../../fixtures/test.js";

/**
 * Unit lifecycle — start / stop / delete via the detail page.
 *
 * Mirrors `tests/e2e/scenarios/fast/07-create-start-unit.sh`.
 */

interface UnitStatusResponse {
  unit: { name: string };
  status?: { lifecycleStatus?: string } | null;
}

test.describe("units — lifecycle (start / stop / delete)", () => {
  test("start transitions the unit to Running (or Starting), stop returns it to Stopped", async ({
    page,
    tracker,
  }) => {
    const name = tracker.unit(unitName("lifecycle"));
    await apiPost("/api/v1/tenant/units", {
      name,
      displayName: name,
      description: "Lifecycle spec (e2e-portal)",
      tool: TOOL_ID,
      provider: PROVIDER_ID,
      model: DEFAULT_MODEL,
      hosting: "ephemeral",
      isTopLevel: true,
    });

    await page.goto(`/units/${name}`);
    await expect(page.getByRole("heading", { name })).toBeVisible();

    // Start button — usually labelled "Start unit" or "Start".
    await page.getByRole("button", { name: /^start( unit)?$/i }).click();

    // Poll the API for lifecycle transition; the UI status pill follows
    // the actor's reported status, so polling the API is the
    // deterministic signal.
    await expect
      .poll(
        async () => {
          const detail = await apiGet<UnitStatusResponse>(
            `/api/v1/tenant/units/${encodeURIComponent(name)}`,
          );
          return detail.status?.lifecycleStatus ?? "";
        },
        { timeout: 30_000, intervals: [500, 1000, 2000] },
      )
      .toMatch(/Running|Starting/);

    // Stop.
    await page.getByRole("button", { name: /^stop( unit)?$/i }).click();
    await expect
      .poll(
        async () => {
          const detail = await apiGet<UnitStatusResponse>(
            `/api/v1/tenant/units/${encodeURIComponent(name)}`,
          );
          return detail.status?.lifecycleStatus ?? "";
        },
        { timeout: 30_000, intervals: [500, 1000, 2000] },
      )
      .toMatch(/Stopped|Stopping/);
  });

  test("delete from the detail page removes the unit", async ({
    page,
    tracker,
  }) => {
    const name = tracker.unit(unitName("delete"));
    await apiPost("/api/v1/tenant/units", {
      name,
      displayName: name,
      description: "Delete spec (e2e-portal)",
      tool: TOOL_ID,
      provider: PROVIDER_ID,
      model: DEFAULT_MODEL,
      hosting: "ephemeral",
      isTopLevel: true,
    });

    await page.goto(`/units/${name}`);
    await page.getByRole("button", { name: /^(delete|delete unit|remove)$/i }).first().click();
    // Destructive confirmation dialog.
    const confirm = page.getByRole("button", { name: /^(delete|confirm|yes, delete)$/i });
    await confirm.last().click();

    // After delete the page redirects to /units (or shows a "not found" state).
    await page.waitForURL(/\/units(\/|\?|$)/, { timeout: 30_000 });

    // Cross-check: list does not include the unit.
    await page.goto("/units");
    await expect(page.getByText(name)).toHaveCount(0, { timeout: 10_000 });
  });
});
