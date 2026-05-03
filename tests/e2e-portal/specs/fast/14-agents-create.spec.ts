import { apiPost } from "../../fixtures/api.js";
import { agentName, unitName } from "../../fixtures/ids.js";
import { DEFAULT_MODEL, PROVIDER_ID, TOOL_ID } from "../../fixtures/runtime.js";
import { expect, test } from "../../fixtures/test.js";
import { createAgent } from "../../helpers/agent-create.js";

/**
 * Agents — create flow.
 *
 * Pre-seeds a unit because every agent must belong to ≥1 unit (#744).
 */

test.describe("agents — create page", () => {
  test("creates an agent assigned to a single unit; lands on /agents", async ({
    page,
    tracker,
  }) => {
    const unit = tracker.unit(unitName("agent-host"));
    const aId = tracker.agent(agentName("ada"));

    await apiPost("/api/v1/tenant/units", {
      name: unit,
      displayName: unit,
      description: "Agent host (e2e-portal)",
      tool: TOOL_ID,
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

    // Cross-check the list view. The page renders either `agents-grid`
    // (when at least one agent matches the active filters) or
    // `agents-empty` otherwise; the agent-id text is the load-bearing
    // assertion either way.
    await page.goto("/agents");
    await expect(
      page.getByTestId("agents-grid").or(page.getByTestId("agents-empty")),
    ).toBeVisible();
    await expect(page.getByText(aId).first()).toBeVisible();
  });

  test("submits with no units selected and surfaces the validation error", async ({
    page,
  }) => {
    await page.goto("/agents/create");

    await page.getByLabel("Agent id").fill(agentName("nounit"));
    await page.getByLabel("Display name").fill("No Unit");

    await page.getByRole("button", { name: /^create agent$|^create$/i }).click();
    await expect(page.getByTestId("agent-create-error")).toBeVisible({
      timeout: 5_000,
    });
  });
});
