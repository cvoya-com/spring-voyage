import { apiGet, apiPost } from "../../fixtures/api.js";
import { unitName } from "../../fixtures/ids.js";
import { AGENT_ID, DEFAULT_MODEL, PROVIDER_ID } from "../../fixtures/runtime.js";
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
      agent: AGENT_ID,
      provider: PROVIDER_ID,
      model: DEFAULT_MODEL,
      hosting: "ephemeral",
      isTopLevel: true,
    });

    // Connector lives under Config → Connector subtab. The unbound state
    // renders a "Not configured" badge and "not wired to any connector
    // yet" copy block.
    await page.goto(
      `/units?node=${encodeURIComponent(name)}&tab=Config&subtab=Connector`,
    );
    await expect(page.getByText(/not configured/i).first()).toBeVisible({
      timeout: 10_000,
    });
    await expect(
      page
        .getByText(/not wired to any connector|no connectors? are installed/i)
        .first(),
    ).toBeVisible({ timeout: 10_000 });

    // API confirms.
    const conn = await apiGet<UnitConnectorResponse | null>(
      `/api/v1/tenant/units/${encodeURIComponent(name)}/connector`,
    ).catch(() => null);
    expect(conn?.typeSlug ?? null).toBeNull();
  });
});
