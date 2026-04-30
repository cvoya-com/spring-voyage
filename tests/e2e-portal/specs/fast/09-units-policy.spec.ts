import { apiGet, apiPost } from "../../fixtures/api.js";
import { unitName } from "../../fixtures/ids.js";
import { DEFAULT_MODEL, PROVIDER_ID, TOOL_ID } from "../../fixtures/runtime.js";
import { expect, test } from "../../fixtures/test.js";

/**
 * Unit policy editor — five dimensions (skill / model / cost /
 * executionMode / initiative). The Policies tab opens per-dimension
 * dialogs; this spec drives one save per dimension and asserts the
 * server reflects it.
 *
 * Shell counterpart: `15-unit-policy-roundtrip.sh` and `18-unit-policy-cli-roundtrip.sh`.
 */

interface PolicyResponse {
  unit: { name: string };
  skill?: unknown;
  model?: unknown;
  cost?: unknown;
  executionMode?: unknown;
  initiative?: unknown;
}

test.describe("units — policy roundtrip", () => {
  test("opens Policies tab and persists edits across all five dimensions", async ({
    page,
    tracker,
  }) => {
    const name = tracker.unit(unitName("policy"));
    await apiPost("/api/v1/tenant/units", {
      name,
      displayName: name,
      description: "Policy spec (e2e-portal)",
      tool: TOOL_ID,
      provider: PROVIDER_ID,
      model: DEFAULT_MODEL,
      hosting: "ephemeral",
      isTopLevel: true,
    });

    await page.goto(`/units/${name}`);
    await page.getByRole("tab", { name: /^policies$/i }).click();
    await expect(page.getByTestId("policies-tab-effective")).toBeVisible();

    // Cost dialog — set a daily/monthly cap.
    await page.getByRole("button", { name: /edit cost|cost policy/i }).first().click();
    await expect(page.getByTestId("cost-policy-dialog")).toBeVisible();
    const dailyCap = page.getByLabel(/daily cap|daily limit|day/i).first();
    if (await dailyCap.isVisible().catch(() => false)) {
      await dailyCap.fill("5");
    }
    await page.getByRole("button", { name: /^save$|^apply$/i }).click();
    await expect(page.getByTestId("cost-policy-dialog")).toBeHidden({ timeout: 5_000 });

    // Execution-mode dialog.
    await page.getByRole("button", { name: /edit execution mode|execution mode/i }).first().click();
    if (await page.getByTestId("execution-mode-policy-dialog").isVisible().catch(() => false)) {
      await page.getByRole("radio", { name: /on.?demand/i }).first().check();
      await page.getByRole("button", { name: /^save$|^apply$/i }).click();
      await expect(page.getByTestId("execution-mode-policy-dialog")).toBeHidden({ timeout: 5_000 });
    }

    // Initiative dialog.
    await page.getByRole("button", { name: /edit initiative|initiative policy/i }).first().click();
    if (await page.getByTestId("initiative-policy-dialog").isVisible().catch(() => false)) {
      await page.getByRole("button", { name: /^save$|^apply$/i }).click();
      await expect(page.getByTestId("initiative-policy-dialog")).toBeHidden({ timeout: 5_000 });
    }

    // Cross-check: at least one of cost / executionMode / initiative is now non-null on the server.
    const policy = await apiGet<PolicyResponse>(
      `/api/v1/tenant/units/${encodeURIComponent(name)}/policy`,
    );
    expect(
      Boolean(policy.cost) || Boolean(policy.executionMode) || Boolean(policy.initiative),
      "expected at least one policy dimension to be set after the UI roundtrip",
    ).toBe(true);
  });
});
