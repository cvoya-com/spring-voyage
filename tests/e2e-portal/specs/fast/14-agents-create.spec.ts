import { apiPost } from "../../fixtures/api.js";
import { agentName, unitName } from "../../fixtures/ids.js";
import { AGENT_ID, DEFAULT_MODEL, PROVIDER_ID } from "../../fixtures/runtime.js";
import { expect, test } from "../../fixtures/test.js";
import {
  createAgent,
  openScratchAgentCreate,
} from "../../helpers/agent-create.js";

/**
 * Agents — create flow.
 *
 * Covers the standalone `/agents/create` scratch path through the extracted
 * form component.
 */

test.describe("agents — create page", () => {
  test("creates an agent assigned to a single unit", async ({
    page,
    tracker,
  }) => {
    const unit = tracker.unit(unitName("agent-host"));
    const aId = tracker.agent(agentName("ada"));

    await apiPost("/api/v1/tenant/units", {
      name: unit,
      displayName: unit,
      description: "Agent host (e2e-portal)",
      agent: AGENT_ID,
      provider: PROVIDER_ID,
      model: DEFAULT_MODEL,
      hosting: "ephemeral",
      isTopLevel: true,
    });

    await createAgent(page, {
      id: aId,
      displayName: "Ada Lovelace",
      role: "reviewer",
      unitNames: [unit],
    });

    // Cross-check the list view. The list cards render the display name as
    // the visible identifier, while the stable agent id remains in the API.
    await page.goto("/agents");
    await expect(
      page.getByTestId("agents-grid").or(page.getByTestId("agents-empty")),
    ).toBeVisible();
    await expect(page.getByText("Ada Lovelace").first()).toBeVisible();
  });

  test("shows inherited execution affordances before create", async ({
    page,
    tracker,
  }) => {
    const unit = tracker.unit(unitName("agent-inherit"));
    const aId = tracker.agent(agentName("inherit"));

    await apiPost("/api/v1/tenant/units", {
      name: unit,
      displayName: unit,
      description: "Agent inherit spec (e2e-portal)",
      agent: AGENT_ID,
      provider: PROVIDER_ID,
      model: DEFAULT_MODEL,
      hosting: "ephemeral",
      isTopLevel: true,
    });

    await openScratchAgentCreate(page);

    const executionBadge = page.getByTestId("execution-card-badge");
    await expect(executionBadge).toBeVisible();
    await expect(executionBadge).toHaveText("Inherits");
    await expect(page.getByTestId("inherit-indicator").first()).toBeVisible();

    await page.getByLabel("Agent id").fill(aId);
    await page.getByLabel("Display name").fill("Inherit Agent");
    await page
      .getByRole("checkbox", { name: new RegExp(`assign to ${unit}`, "i") })
      .first()
      .check();

    await page.getByRole("button", { name: /^create agent$|^create$/i }).click();
    await page.waitForURL((url) => !url.pathname.endsWith("/agents/create"), {
      timeout: 60_000,
    });

    await page.goto("/agents");
    await expect(
      page.getByTestId("agents-grid").or(page.getByTestId("agents-empty")),
    ).toBeVisible();
    await expect(page.getByText("Inherit Agent").first()).toBeVisible();
  });
});
