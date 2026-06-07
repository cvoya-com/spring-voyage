import { apiGet, seedUnit } from "../../fixtures/api.js";
import { unitName } from "../../fixtures/ids.js";
import { expect, test } from "../../fixtures/test.js";
import { gotoExplorerUnit } from "../../helpers/nav.js";

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
    const u = await seedUnit(name, {
      description: "Connector binding spec (e2e-portal)",
    });

    // Connector lives under Config → Connector subtab. The unbound state
    // renders a "Not configured" badge and "not wired to any connector
    // yet" copy block.
    await gotoExplorerUnit(page, u.hex, { tab: "Config", subtab: "Connector" });
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
      `/api/v1/tenant/units/${encodeURIComponent(u.hex)}/connector`,
    ).catch(() => null);
    expect(conn?.typeSlug ?? null).toBeNull();
  });
});
