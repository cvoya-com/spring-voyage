import { apiGet, apiPost } from "../../fixtures/api.js";
import { agentName, unitName } from "../../fixtures/ids.js";
import { AGENT_ID, DEFAULT_MODEL, PROVIDER_ID } from "../../fixtures/runtime.js";
import { expect, test } from "../../fixtures/test.js";

/**
 * Unit's Agents tab → create-agent dialog (create / remove).
 *
 * Mirrors `tests/e2e/scenarios/fast/06-unit-membership-roundtrip.sh`. The
 * shell scenario asserts the CLI, /memberships, and /agents read paths agree;
 * this spec exercises the portal's unit-tab create flow and confirms the row
 * appears + the API reflects it.
 */

interface MembershipResponse {
  agentAddress: string;
}

interface AgentListResponse {
  name: string;
}

test.describe("units — agents tab membership", () => {
  test("create an agent in a unit, see row, remove it", async ({
    page,
    tracker,
  }) => {
    const unitB = tracker.unit(unitName("memb"));
    const aId = tracker.agent(agentName("memb-ada"));

    // Seed the unit. The Agents tab Add action now opens AgentCreateDialog,
    // so the agent itself is created through the portal rather than picked
    // from an existing-agent dropdown.
    await apiPost("/api/v1/tenant/units", {
      name: unitB,
      displayName: unitB,
      description: "Membership spec unit (e2e-portal)",
      agent: AGENT_ID,
      provider: PROVIDER_ID,
      model: DEFAULT_MODEL,
      hosting: "ephemeral",
      isTopLevel: true,
    });

    // Open unit → Agents tab → "Add agent". The trigger opens the shared
    // create-agent dialog preselected to this unit.
    await page.goto(
      `/units?node=${encodeURIComponent(unitB)}&tab=Agents`,
    );
    await page.getByLabel("Add agent", { exact: true }).click();

    const dialog = page.getByRole("dialog", {
      name: new RegExp(`Create agent in ${unitB}`, "i"),
    });
    await expect(
      dialog.getByTestId("agent-create-dialog-unit-strip"),
    ).toContainText(unitB);
    await dialog.getByRole("textbox", { name: /^Agent id$/i }).fill(aId);
    await dialog.getByRole("textbox", { name: /^Display name$/i }).fill(aId);
    await expect(
      dialog.getByRole("checkbox", {
        name: new RegExp(`Assign to ${unitB}`, "i"),
      }),
    ).toBeChecked();
    await dialog.getByTestId("agent-create-submit").click();

    // Membership row testid.
    await expect(
      page
        .locator('[data-testid^="unit-membership-"]')
        .filter({ hasText: aId })
        .first(),
    ).toBeVisible({ timeout: 60_000 });

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

  test("creates an agent with inherited execution config (no overrides)", async ({
    page,
    tracker,
  }) => {
    const unitB = tracker.unit(unitName("memb-inherit"));
    const aId = tracker.agent(agentName("memb-inherit-ada"));

    await apiPost("/api/v1/tenant/units", {
      name: unitB,
      displayName: unitB,
      description: "Membership inherit-only spec unit (e2e-portal)",
      agent: AGENT_ID,
      provider: PROVIDER_ID,
      model: DEFAULT_MODEL,
      hosting: "ephemeral",
      isTopLevel: true,
    });

    await page.goto(
      `/units?node=${encodeURIComponent(unitB)}&tab=Agents`,
    );
    await page.getByLabel("Add agent", { exact: true }).click();

    const dialog = page.getByRole("dialog", {
      name: new RegExp(`Create agent in ${unitB}`, "i"),
    });
    await expect(
      dialog.getByTestId("agent-create-dialog-unit-strip"),
    ).toContainText(unitB);

    await dialog.getByRole("textbox", { name: /^Agent id$/i }).fill(aId);
    await dialog.getByRole("textbox", { name: /^Display name$/i }).fill(aId);
    await expect(
      dialog.getByRole("checkbox", {
        name: new RegExp(`Assign to ${unitB}`, "i"),
      }),
    ).toBeChecked();

    // Leave every execution field unset; the card should remain in inherit mode.
    await expect(dialog.getByTestId("execution-card-badge")).toContainText(
      /Inherits/i,
    );

    await dialog.getByTestId("agent-create-submit").click();

    await expect(
      page
        .locator('[data-testid^="unit-membership-"]')
        .filter({ hasText: aId })
        .first(),
    ).toBeVisible({ timeout: 60_000 });

    const agents = await apiGet<AgentListResponse[]>("/api/v1/tenant/agents");
    expect(agents.find((agent) => agent.name === aId)).toBeDefined();
  });
});
