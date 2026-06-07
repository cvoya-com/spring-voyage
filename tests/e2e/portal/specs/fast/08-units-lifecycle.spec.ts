import { apiGet, apiPut, seedUnit } from "../../fixtures/api.js";
import { unitName } from "../../fixtures/ids.js";
import { AGENT_ID } from "../../fixtures/runtime.js";
import { expect, test } from "../../fixtures/test.js";
import { gotoExplorerUnit } from "../../helpers/nav.js";

/**
 * Unit lifecycle — start / stop / delete via the detail page.
 *
 * Mirrors `tests/e2e/scenarios/fast/07-create-start-unit.sh`.
 */

interface UnitStatusResponse {
  // Wire shape returned by GET /api/v1/tenant/units/{id}: a top-level
  // `unit` object with the lifecycle string in `unit.status`. The
  // duplicated `details.Status` is the actor-side echo.
  unit: { name: string; status?: string | null };
  details?: { Status?: string | null } | null;
}

test.describe("units — lifecycle (start / stop / delete)", () => {
  test("Validate kicks off the validation workflow (Draft → Validating)", async ({
    page,
    tracker,
  }) => {
    // The full Draft → Validating → Stopped → Running → Stopped
    // round-trip exercises the validation workflow + container
    // dispatcher; that path is flaky on a cold stack (workflow
    // registry race + image-pull cost). The spec deliberately
    // narrows to the click-triggers-transition contract — the rest
    // is covered by the shell suite (`tests/e2e/scenarios/fast/07`)
    // which can wait minutes without a Playwright timeout in the
    // way.
    const name = tracker.unit(unitName("lifecycle"));
    const u = await seedUnit(name, {
      description: "Lifecycle spec (e2e-portal)",
    });
    // Validation requires image + runtime — `image` / `runtime`
    // aren't on `CreateUnitRequest`; they live on the separate
    // execution-defaults endpoint (keyed on the hex id). Without these
    // the workflow surfaces "ConfigurationIncomplete: missing image"
    // and the unit transitions Validating → Error in <1s, which is the
    // failure mode this spec tolerates.
    await apiPut(
      `/api/v1/tenant/units/${encodeURIComponent(u.hex)}/execution`,
      {
        image: "ghcr.io/cvoya-com/spring-voyage-agent:latest",
        runtime: AGENT_ID,
      },
    );

    await gotoExplorerUnit(page, u.hex, { tab: "Overview" });
    await expect(page.getByTestId("detail-title")).toContainText(name);

    // Click Validate; the unit must leave Draft. We accept any
    // non-Draft status (Validating / Stopped / Running / Error) so
    // the workflow's downstream behaviour doesn't gate this spec.
    await page.getByTestId("unit-action-validate").click();
    await expect
      .poll(
        async () => {
          const detail = await apiGet<UnitStatusResponse>(
            `/api/v1/tenant/units/${encodeURIComponent(u.hex)}`,
          );
          return detail.unit?.status ?? detail.details?.Status ?? "";
        },
        { timeout: 30_000, intervals: [500, 1000, 2000] },
      )
      .not.toBe("Draft");
  });

  test("delete from the detail page removes the unit", async ({
    page,
    tracker,
  }) => {
    const name = tracker.unit(unitName("delete"));
    const u = await seedUnit(name, { description: "Delete spec (e2e-portal)" });

    await gotoExplorerUnit(page, u.hex, { tab: "Overview" });
    await page.getByTestId("unit-action-delete").click();
    // Confirmation dialog uses the canonical "Permanently delete" label
    // (see `ConfirmDialog` in unit-pane-actions.tsx).
    await page
      .getByRole("dialog")
      .getByRole("button", { name: /Permanently delete/i })
      .click();
    // The explorer holds an open SSE stream, so `networkidle` never
    // settles — poll the API directly instead (below) for the
    // authoritative delete signal.

    // After delete the page redirects to the explorer. Cross-check via
    // API — the explorer's tenant-tree response is cache-controlled
    // (max-age=15s) so a UI-side read can race the cache. The API
    // endpoint is the authoritative read.
    await expect
      .poll(
        async () => {
          const res = await fetch(
            `${process.env.PLAYWRIGHT_BASE_URL ?? "http://localhost"}/api/v1/tenant/units/${encodeURIComponent(u.hex)}`,
          );
          return res.status;
        },
        { timeout: 15_000, intervals: [500, 1000, 2000] },
      )
      .toBe(404);
  });
});
