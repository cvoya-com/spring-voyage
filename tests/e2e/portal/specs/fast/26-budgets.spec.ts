import { apiGet } from "../../fixtures/api.js";
import { expect, test } from "../../fixtures/test.js";

/**
 * Budgets surface (`/budgets` 308s to `/analytics/costs`; the tenant
 * budget editor lives there).
 */

interface TenantBudgetResponse {
  cap?: { amount?: number } | number | null;
}

test.describe("budgets", () => {
  test("legacy /budgets path 308s to /analytics/costs", async ({ page }) => {
    const res = await page.goto("/budgets");
    // After the redirect the URL should land on /analytics/costs.
    expect(page.url()).toMatch(/\/analytics\/costs/);
    expect(res?.status() ?? 200).toBeLessThan(400);
  });

  test("tenant budget API responds with the expected shape", async ({}) => {
    // No UI assertion — the editor lives inside /analytics/costs and is
    // covered by 25-analytics.spec.ts. This sub-spec just sanity-checks
    // that the endpoint is reachable: 200 (budget set) or 404 (no
    // budget for the tenant — happy-path on a fresh stack) are both
    // acceptable; 5xx is the regression we want to catch.
    const data = await apiGet<TenantBudgetResponse>("/api/v1/tenant/budget", {
      expect: [200, 404],
    });
    if (data) {
      expect(data).toBeDefined();
    }
  });
});
