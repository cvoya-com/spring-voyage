import { apiGet, apiPost } from "../../fixtures/api.js";
import { agentName, unitName } from "../../fixtures/ids.js";
import { AGENT_ID, DEFAULT_MODEL, PROVIDER_ID } from "../../fixtures/runtime.js";
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
      agent: AGENT_ID,
      provider: PROVIDER_ID,
      model: DEFAULT_MODEL,
      hosting: "ephemeral",
      isTopLevel: true,
    });
    await apiPost("/api/v1/tenant/units", {
      name: unitB,
      displayName: unitB,
      description: "Membership spec — unit B (e2e-portal)",
      agent: AGENT_ID,
      provider: PROVIDER_ID,
      model: DEFAULT_MODEL,
      hosting: "ephemeral",
      isTopLevel: true,
    });
    await apiPost("/api/v1/tenant/agents", {
      name: aId,
      displayName: aId,
      description: "Membership spec (e2e-portal)",
      unitIds: [unitA],
    });

    // Open unit B → Agents tab → "Add agent". Two buttons share the
    // "Add agent" label — the trigger on the panel header (with
    // aria-label) and the submit inside the dialog. Open by clicking
    // the labelled trigger; submit through the dialog scope.
    await page.goto(
      `/units?node=${encodeURIComponent(unitB)}&tab=Agents`,
    );
    await page.getByLabel("Add agent", { exact: true }).click();

    // Membership dialog → pick the existing agent. The dialog uses a
    // `<select>` (`aria-label="Agent"`) populated with assignable
    // agents — a true searchable combobox is tracked separately.
    const dialog = page.getByRole("dialog");
    await dialog
      .getByRole("combobox", { name: /^Agent$/i })
      .selectOption(aId);
    await dialog.getByRole("button", { name: /^Add agent$/i }).click();

    // Membership row testid.
    await expect(page.getByTestId(new RegExp(`^unit-membership-`)).first()).toBeVisible({
      timeout: 10_000,
    });

    // Cross-check API.
    const memberships = await apiGet<MembershipResponse[]>(
      `/api/v1/tenant/units/${encodeURIComponent(unitB)}/memberships`,
    );
    expect(memberships.find((m) => m.agentAddress.includes(aId))).toBeDefined();

    // Remove via UI — the row exposes a per-membership "remove" button
    // testid'd on the agent address; clicking it opens a confirmation
    // dialog whose confirm action sits inside `role="dialog"` (so we
    // don't pick up the page-level `unit-action-delete` button).
    const row = page
      .locator('[data-testid^="unit-membership-"]')
      .filter({ hasText: aId })
      .first();
    await row.getByTestId(/^unit-membership-remove-/).click();
    const confirmDialog = page.getByRole("dialog");
    if (await confirmDialog.isVisible().catch(() => false)) {
      await confirmDialog
        .getByRole("button", { name: /^(remove|delete|confirm|unassign)$/i })
        .first()
        .click();
    }
    await expect(row).toHaveCount(0, { timeout: 10_000 });
  });
});
