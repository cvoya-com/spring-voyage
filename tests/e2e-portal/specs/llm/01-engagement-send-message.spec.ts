import { apiPost, apiPut } from "../../fixtures/api.js";
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
    // Cold-start LLM + dapr-agent container pull on the first turn can
    // run well past the global per-test default; raise the cap so the
    // legitimate slow path doesn't trip the test on a cold runner.
    test.setTimeout(360_000);
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
    // Set image + runtime defaults so ephemeral agent dispatch picks
    // up a working container image (otherwise the dispatch fails with
    // "Ephemeral agent requires a container image"). image/runtime
    // aren't on `CreateUnitRequest`; they live on `/execution`.
    await apiPut(
      `/api/v1/tenant/units/${encodeURIComponent(unit)}/execution`,
      { image: "localhost/spring-dapr-agent", runtime: "podman" },
    );
    await apiPost("/api/v1/tenant/agents", {
      name: agent,
      displayName: "Engagement Spec Agent",
      description: "Engagement send-message spec (e2e-portal)",
      unitIds: [unit],
    });

    // Kick off a thread by sending a free-form message via /messages.
    // The endpoint auto-generates a thread id when none is supplied.
    // Wire shape (`SendMessageRequest`): { to, type, payload, threadId? }.
    const seed = await apiPost<MessageResponse>("/api/v1/tenant/messages", {
      to: { scheme: "agent", path: agent },
      type: "Domain",
      payload: { text: "Hello from e2e-portal" },
    });
    expect(seed.threadId).toBeTruthy();

    // Open the engagement detail. The portal redesign in #1500 lands
    // the timeline + composer on /engagement/<id>, with the dropdown
    // defaulting to "Messages" so the natural-language dialog is
    // visible without lifecycle/tool noise.
    await page.goto(`/engagement/${seed.threadId}`);
    await expect(page.getByTestId("engagement-detail-page")).toBeVisible();

    // Switch to "Full timeline" so we can see every event (including
    // the seed Domain message and any agent-emitted lifecycle events).
    // The dropdown lives top-right of the timeline.
    const filterTrigger = page.getByTestId("timeline-filter-trigger");
    if (await filterTrigger.isVisible().catch(() => false)) {
      await filterTrigger.click();
      await page.getByTestId("timeline-filter-option-full").click();
    }

    // Assertion 1 — closes the #1465 dispatch round-trip gap on the
    // portal side: an agent-authored event must land in the timeline
    // after the API-side seed. The composer-hidden case (when the
    // sender ends up classified as Observer) is no longer a reason
    // to skip — the seed dispatch alone is enough to prove the
    // dispatcher → agent transport works.
    await expect
      .poll(
        async () =>
          await page
            .getByTestId("engagement-timeline-events")
            .locator('[data-role="agent"]')
            .count(),
        {
          // Cold-start cap: dapr-agent container pull (when not cached)
          // + Ollama warmup + the LLM turn itself can comfortably exceed
          // 90s on a slow runner. Match the killer use-case timeout
          // (240s) so a legitimately slow first turn is not flagged as
          // a regression.
          timeout: 240_000,
          intervals: [2000, 5000, 10_000],
          message:
            "Expected at least one agent-authored event in the timeline after the seeded message — the dispatcher → agent JSON-RPC round-trip looks broken (#1465).",
        },
      )
      .toBeGreaterThan(0);

    // Assertion 2 — when the composer is exposed (the human is a
    // participant), drive a second message through the UI and verify
    // a new event lands. When the composer isn't exposed (the human
    // ended up classified as Observer; tracked by #1292), the
    // assertion above already covers the dispatch path so we skip
    // the UI-driven send rather than the whole spec.
    const composer = page.getByTestId("engagement-composer");
    if (!(await composer.isVisible().catch(() => false))) {
      test
        .info()
        .annotations.push({
          type: "composer-hidden",
          description:
            "Composer not exposed (sender classified as Observer; tracked by #1292) — UI-driven send skipped, but the dispatch round-trip assertion above ran.",
        });
      return;
    }

    const before = await page
      .getByTestId("engagement-timeline-events")
      .locator('[data-testid^="conversation-event-"]')
      .count();

    await composer
      .getByRole("textbox", { name: /message text|your answer/i })
      .fill("Are you there?");
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
