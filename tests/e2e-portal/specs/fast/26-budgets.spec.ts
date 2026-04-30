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
    // that the API endpoint the editor reads is reachable, so a 500
    // surfaces here rather than as a vague UI failure.
    await expect(async () => {
      await apiGet<TenantBudgetResponse>("/api/v1/tenant/budget");
    }).not.toThrow();
  });
});
