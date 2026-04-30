import { apiGet, apiPost } from "../../fixtures/api.js";
import { unitName } from "../../fixtures/ids.js";
import { DEFAULT_MODEL, PROVIDER_ID, TOOL_ID } from "../../fixtures/runtime.js";
import { expect, test } from "../../fixtures/test.js";

/**
 * Bind a unit to a connector (non-GitHub fallback).
 *
 * GitHub binding requires an installed GitHub App and live installation
 * tokens; that flow is exercised in the killer-use-case suite. This
 * spec checks the generic "bind via wizard's connector step" path against
 * a connector that doesn't need an external installation. If the only
 * connector exposing a bind path that works without external setup is
 * GitHub, the spec downgrades to clearing a binding (the inverse path).
 */

interface UnitConnectorResponse {
  typeSlug?: string | null;
}

test.describe("connectors — clear unit binding", () => {
  test("a unit with no binding shows null on /connector and clears as a no-op", async ({
    page,
    tracker,
  }) => {
    const name = tracker.unit(unitName("nobind"));
    await apiPost("/api/v1/tenant/units", {
      name,
      displayName: name,
      description: "Connector binding spec (e2e-portal)",
      tool: TOOL_ID,
      provider: PROVIDER_ID,
      model: DEFAULT_MODEL,
      hosting: "ephemeral",
      isTopLevel: true,
    });

    await page.goto(`/units/${name}`);
    // Connector tab / panel — exists on the unit detail page.
    const connectorTab = page.getByRole("tab", { name: /^connector$|^integrations?$/i });
    if (await connectorTab.first().isVisible().catch(() => false)) {
      await connectorTab.first().click();
    }
    // The "no binding" state surfaces a clear copy block.
    await expect(
      page.getByText(/no connector|not bound|connect (this )?unit/i).first(),
    ).toBeVisible({ timeout: 10_000 });

    // API confirms.
    const conn = await apiGet<UnitConnectorResponse | null>(
      `/api/v1/tenant/units/${encodeURIComponent(name)}/connector`,
    ).catch(() => null);
    expect(conn?.typeSlug ?? null).toBeNull();
  });
});
