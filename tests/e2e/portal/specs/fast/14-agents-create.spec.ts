import { seedUnit } from "../../fixtures/api.js";
import { agentName, unitName } from "../../fixtures/ids.js";
import { expect, test } from "../../fixtures/test.js";
import {
  createAgent,
  openScratchAgentCreate,
} from "../../helpers/agent-create.js";

/**
 * Agents — create flow.
 *
 * Covers the standalone `/agents/create` scratch path through the extracted
 * form component (`<AgentCreateForm context="page">`). Agents are identified
 * by displayName + a server-assigned hex; there is no operator-supplied id.
 */

test.describe("agents — create page", () => {
  test("creates an agent assigned to a single unit", async ({
    page,
    tracker,
  }) => {
    const unit = tracker.unit(unitName("agent-host"));
    const ada = tracker.agent(agentName("ada"));

    await seedUnit(unit, { description: "Agent host (e2e-portal)" });

    await createAgent(page, {
      displayName: ada,
      role: "reviewer",
      unitDisplayNames: [unit],
    });

    // Cross-check the list view. The list cards render the display name as
    // the visible identifier, while the stable hex id remains in the API.
    await page.goto("/agents");
    await expect(
      page.getByTestId("agents-grid").or(page.getByTestId("agents-empty")),
    ).toBeVisible();
    await expect(page.getByText(ada).first()).toBeVisible();
  });

  test("shows inherited execution affordances before create", async ({
    page,
    tracker,
  }) => {
    const unit = tracker.unit(unitName("agent-inherit"));
    const inherit = tracker.agent(agentName("inherit"));

    await seedUnit(unit, { description: "Agent inherit spec (e2e-portal)" });

    await openScratchAgentCreate(page);

    const executionBadge = page.getByTestId("execution-card-badge");
    await expect(executionBadge).toBeVisible();
    await expect(executionBadge).toHaveText("Inherits");
    await expect(page.getByTestId("inherit-indicator").first()).toBeVisible();

    await page.getByLabel("Display name").fill(inherit);
    await page
      .getByRole("checkbox", { name: new RegExp(`assign to ${unit}`, "i") })
      .first()
      .check();

    await page.getByTestId("agent-create-submit").click();
    await page.waitForURL((url) => !url.pathname.endsWith("/agents/create"), {
      timeout: 60_000,
    });

    await page.goto("/agents");
    await expect(
      page.getByTestId("agents-grid").or(page.getByTestId("agents-empty")),
    ).toBeVisible();
    await expect(page.getByText(inherit).first()).toBeVisible();
  });
});
