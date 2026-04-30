import { apiPost } from "../../fixtures/api.js";
import { agentName, unitName } from "../../fixtures/ids.js";
import { DEFAULT_MODEL, PROVIDER_ID, TOOL_ID } from "../../fixtures/runtime.js";
import { expect, test } from "../../fixtures/test.js";

/**
 * Engagement — send a message and observe a timeline event.
 *
 * Requires the live LLM (Ollama) so the agent's turn returns a real
 * response. Pre-seeds a unit + agent, kicks off a thread via the API
 * (so the engagement exists), then drives the composer in the browser
 * and asserts a new event lands in the timeline.
 */

interface MessageResponse {
  threadId: string;
}

test.describe("engagement — send message via composer", () => {
  test("composer sends, timeline shows the new event", async ({
    page,
    tracker,
    ollamaUp,
  }) => {
    void ollamaUp;
    const unit = tracker.unit(unitName("eng-msg"));
    const agent = tracker.agent(agentName("eng-msg-ada"));

    await apiPost("/api/v1/tenant/units", {
      name: unit,
      displayName: unit,
      description: "Engagement send-message spec (e2e-portal)",
      tool: TOOL_ID,
      provider: PROVIDER_ID,
      model: DEFAULT_MODEL,
      hosting: "ephemeral",
      isTopLevel: true,
    });
    await apiPost("/api/v1/tenant/agents", {
      id: agent,
      displayName: "Engagement Spec Agent",
      unitIds: [unit],
    });

    // Kick off a thread by sending a free-form message via /messages.
    // The endpoint auto-generates a thread id when none is supplied.
    const seed = await apiPost<MessageResponse>("/api/v1/tenant/messages", {
      to: { scheme: "agent", path: agent },
      kind: "Domain",
      body: { text: "Hello from e2e-portal" },
    });
    expect(seed.threadId).toBeTruthy();

    // Open the engagement detail.
    await page.goto(`/engagement/${seed.threadId}`);
    await expect(page.getByTestId("engagement-detail-page")).toBeVisible();

    // Capture the initial event count, send a follow-up, and assert growth.
    const before = await page
      .getByTestId("engagement-timeline-events")
      .locator('[data-testid^="conversation-event-"]')
      .count();

    const composer = page.getByTestId("engagement-composer");
    await composer.getByRole("textbox").first().fill("Are you there?");
    await composer.getByRole("button", { name: /^send|submit$/i }).click();

    await expect(async () => {
      const now = await page
        .getByTestId("engagement-timeline-events")
        .locator('[data-testid^="conversation-event-"]')
        .count();
      expect(now).toBeGreaterThan(before);
    }).toPass({ timeout: 60_000, intervals: [500, 1000, 2000] });
  });
});
