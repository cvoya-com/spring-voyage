import {
  apiGet,
  apiPost,
  resolveAgentIdByDisplayName,
  seedUnit,
} from "../../fixtures/api.js";
import { agentName, unitName } from "../../fixtures/ids.js";
import { expect, test } from "../../fixtures/test.js";
import { gotoExplorerUnit } from "../../helpers/nav.js";

/**
 * Unit's Members tab → create-agent dialog (create / remove).
 *
 * Mirrors `tests/e2e/scenarios/fast/06-unit-membership-roundtrip.sh`. The
 * shell scenario asserts the CLI, /memberships, and /agents read paths agree;
 * this spec exercises the portal's unit-tab create flow and confirms the row
 * appears + the API reflects it. Agents are identified by displayName + a
 * server-assigned hex (no operator-supplied id).
 */

interface MembershipResponse {
  // The membership row's canonical reference is the agent's hex address.
  agentAddress: string;
}

interface AgentListItem {
  displayName: string;
}

test.describe("units — agents tab membership", () => {
  test("create an agent in a unit, see row, remove it", async ({
    page,
    tracker,
  }) => {
    const unitB = tracker.unit(unitName("memb"));
    const unitC = tracker.unit(unitName("memb-second"));
    const ada = tracker.agent(agentName("memb-ada"));

    // Seed the unit. The Members tab Add action opens AgentCreateDialog, so
    // the agent itself is created through the portal rather than picked
    // from an existing-agent dropdown.
    const u = await seedUnit(unitB, {
      description: "Membership spec unit (e2e-portal)",
    });
    // A second unit so the agent has two memberships: the platform refuses
    // to remove an agent's LAST unit membership (409 "Agent must belong to
    // at least one unit"), so the remove-from-unit-B step below needs the
    // agent to still belong to unit C afterwards.
    const u2 = await seedUnit(unitC, {
      description: "Membership spec second unit (e2e-portal)",
    });

    // Open unit → Members tab → "Add agent". The trigger opens the shared
    // create-agent dialog preselected to this unit.
    await gotoExplorerUnit(page, u.hex, { tab: "Members" });
    await page.getByTestId("unit-members-add-agent").click();

    const dialog = page.getByRole("dialog", {
      name: new RegExp(`Create agent in ${unitB}`, "i"),
    });
    await expect(
      dialog.getByTestId("agent-create-dialog-unit-strip"),
    ).toContainText(unitB);
    // The form identifies agents by display name only (no "Agent id" field).
    await dialog.getByLabel("Display name").fill(ada);
    await expect(
      dialog.getByRole("checkbox", {
        name: `Assign to ${unitB}`, exact: true,
      }),
    ).toBeChecked();
    await dialog.getByTestId("agent-create-submit").click();

    // Membership row testid (`unit-membership-<agentHex>`); the card text
    // carries the agent's display name.
    await expect(
      page
        .locator('[data-testid^="unit-membership-"]')
        .filter({ hasText: ada })
        .first(),
    ).toBeVisible({ timeout: 60_000 });

    // Cross-check API.
    const memberships = await apiGet<MembershipResponse[]>(
      `/api/v1/tenant/units/${encodeURIComponent(u.hex)}/memberships`,
    );
    expect(memberships.length).toBeGreaterThan(0);

    // Give the agent a second membership (unit C) so removing it from unit
    // B is permitted — otherwise it's the agent's last unit and the server
    // returns 409.
    const agentId = await resolveAgentIdByDisplayName(ada);
    expect(agentId, "newly-created agent should resolve by displayName").not.toBeNull();
    await apiPost(
      `/api/v1/tenant/units/${encodeURIComponent(u2.hex)}/agents/${encodeURIComponent(agentId!)}`,
    );

    // Remove via UI — the row exposes a per-membership "remove" button
    // testid'd on the agent address; clicking it opens a confirmation
    // dialog whose confirm action sits inside `role="dialog"` (so we
    // don't pick up the page-level `unit-action-delete` button).
    const row = page
      .locator('[data-testid^="unit-membership-"]')
      .filter({ hasText: ada })
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
    const ada = tracker.agent(agentName("memb-inherit-ada"));

    const u = await seedUnit(unitB, {
      description: "Membership inherit-only spec unit (e2e-portal)",
    });

    await gotoExplorerUnit(page, u.hex, { tab: "Members" });
    await page.getByTestId("unit-members-add-agent").click();

    const dialog = page.getByRole("dialog", {
      name: new RegExp(`Create agent in ${unitB}`, "i"),
    });
    await expect(
      dialog.getByTestId("agent-create-dialog-unit-strip"),
    ).toContainText(unitB);

    await dialog.getByLabel("Display name").fill(ada);
    await expect(
      dialog.getByRole("checkbox", {
        name: `Assign to ${unitB}`, exact: true,
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
        .filter({ hasText: ada })
        .first(),
    ).toBeVisible({ timeout: 60_000 });

    const agents = await apiGet<AgentListItem[]>("/api/v1/tenant/agents");
    expect(agents.find((agent) => agent.displayName === ada)).toBeDefined();
  });
});
