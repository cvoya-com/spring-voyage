import { apiGet, apiPost, apiPut } from "../../fixtures/api.js";
import { unitName } from "../../fixtures/ids.js";
import { AGENT_ID, DEFAULT_MODEL, PROVIDER_ID } from "../../fixtures/runtime.js";
import { expect, test } from "../../fixtures/test.js";

/**
 * Unit lifecycle — start / stop / delete via the detail page.
 *
 * Mirrors `tests/e2e/scenarios/fast/07-create-start-unit.sh`.
 */

interface UnitStatusResponse {
  // Wire shape returned by GET /api/v1/tenant/units/{name}: a top-level
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
    await apiPost("/api/v1/tenant/units", {
      name,
      displayName: name,
      description: "Lifecycle spec (e2e-portal)",
      agent: AGENT_ID,
      provider: PROVIDER_ID,
      model: DEFAULT_MODEL,
      hosting: "ephemeral",
      isTopLevel: true,
    });
    // Validation requires image + runtime — `image` / `runtime`
    // aren't on `CreateUnitRequest`; they live on the separate
    // execution-defaults endpoint. Without these the workflow
    // surfaces "ConfigurationIncomplete: missing image" and the
    // unit transitions Validating → Error in <1s, which is the
    // failure mode this spec wants to tolerate (tracked separately
    // as a workflow-registry race).
    await apiPut(
      `/api/v1/tenant/units/${encodeURIComponent(name)}/execution`,
      { image: "localhost/spring-dapr-agent", runtime: "podman" },
    );

    await page.goto(`/units/${name}`);
    await expect(page.getByRole("heading", { name })).toBeVisible();

    // Click Validate; the unit must leave Draft. We accept any
    // non-Draft status (Validating / Stopped / Running / Error) so
    // the workflow's downstream behaviour doesn't gate this spec.
    await page.getByTestId("unit-action-validate").click();
    await expect
      .poll(
        async () => {
          const detail = await apiGet<UnitStatusResponse>(
            `/api/v1/tenant/units/${encodeURIComponent(name)}`,
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
    await apiPost("/api/v1/tenant/units", {
      name,
      displayName: name,
      description: "Delete spec (e2e-portal)",
      agent: AGENT_ID,
      provider: PROVIDER_ID,
      model: DEFAULT_MODEL,
      hosting: "ephemeral",
      isTopLevel: true,
    });

    await page.goto(`/units/${name}`);
    await page.getByTestId("unit-action-delete").click();
    // Confirmation dialog uses the canonical "Permanently delete" label
    // (see `ConfirmDialog` in unit-pane-actions.tsx).
    await page
      .getByRole("dialog")
      .getByRole("button", { name: /Permanently delete/i })
      .click();
    // Wait for the delete API to settle. The mutation's onSuccess
    // invalidates the tenant tree, so we need to give it time.
    await page.waitForLoadState("networkidle");

    // After delete the page redirects to /units. Cross-check via API
    // — the explorer's tenant-tree response is cache-controlled
    // (max-age=15s) so a UI-side `getByText` read can race against the
    // cache. The API endpoint is the authoritative read.
    await expect
      .poll(
        async () => {
          const res = await fetch(
            `${process.env.PLAYWRIGHT_BASE_URL ?? "http://localhost"}/api/v1/tenant/units/${encodeURIComponent(name)}`,
          );
          return res.status;
        },
        { timeout: 15_000, intervals: [500, 1000, 2000] },
      )
      .toBe(404);
  });
});
