import { apiGet, apiPost } from "../../fixtures/api.js";
import { agentName, unitName } from "../../fixtures/ids.js";
import { DEFAULT_MODEL, PROVIDER_ID, TOOL_ID } from "../../fixtures/runtime.js";
import { expect, test } from "../../fixtures/test.js";

/**
 * Unit's Agents tab → membership dialog (assign / remove).
 *
 * Mirrors `tests/e2e/scenarios/fast/06-unit-membership-roundtrip.sh`. The
 * shell scenario asserts the CLI, /memberships, and /agents read paths
 * agree; this spec exercises the dialog and confirms the row appears
 * + the API reflects it.
 */

interface MembershipResponse {
  agentAddress: string;
}

test.describe("units — agents tab membership", () => {
  test("add an existing agent to a unit, see row, remove it", async ({
    page,
    tracker,
  }) => {
    const unitA = tracker.unit(unitName("memb-a"));
    const unitB = tracker.unit(unitName("memb-b"));
    const aId = tracker.agent(agentName("memb-ada"));

    // Seed two units and an agent assigned only to unitA. We'll add it to unitB via the UI.
    await apiPost("/api/v1/tenant/units", {
      name: unitA,
      displayName: unitA,
      description: "Membership spec — unit A (e2e-portal)",
      tool: TOOL_ID,
      provider: PROVIDER_ID,
      model: DEFAULT_MODEL,
      hosting: "ephemeral",
      isTopLevel: true,
    });
    await apiPost("/api/v1/tenant/units", {
      name: unitB,
      displayName: unitB,
      description: "Membership spec — unit B (e2e-portal)",
      tool: TOOL_ID,
      provider: PROVIDER_ID,
      model: DEFAULT_MODEL,
      hosting: "ephemeral",
      isTopLevel: true,
    });
    await apiPost("/api/v1/tenant/agents", {
      id: aId,
      displayName: aId,
      unitIds: [unitA],
    });

    // Open unit B → Agents tab → "Add agent".
    await page.goto(`/units/${unitB}`);
    await page.getByRole("tab", { name: /^agents$/i }).click();
    await page.getByRole("button", { name: /^(add agent|new agent|assign agent)$/i }).first().click();

    // Membership dialog → pick the existing agent.
    await page.getByRole("textbox", { name: /search|filter|agent/i }).first().fill(aId);
    await page.getByText(aId).first().click();
    await page.getByRole("button", { name: /^(add|assign|save)$/i }).first().click();

    // Membership row testid.
    await expect(page.getByTestId(new RegExp(`^unit-membership-`)).first()).toBeVisible({
      timeout: 10_000,
    });

    // Cross-check API.
    const memberships = await apiGet<MembershipResponse[]>(
      `/api/v1/tenant/units/${encodeURIComponent(unitB)}/memberships`,
    );
    expect(memberships.find((m) => m.agentAddress.includes(aId))).toBeDefined();

    // Remove via UI — find the matching row and click remove.
    const row = page
      .locator('[data-testid^="unit-membership-"]')
      .filter({ hasText: aId })
      .first();
    await row.getByRole("button", { name: /remove|delete|unassign/i }).first().click();
    const confirm = page.getByRole("button", { name: /^(remove|delete|confirm|unassign)$/i });
    if (await confirm.first().isVisible().catch(() => false)) {
      await confirm.first().click();
    }
    await expect(row).toHaveCount(0, { timeout: 10_000 });
  });
});
