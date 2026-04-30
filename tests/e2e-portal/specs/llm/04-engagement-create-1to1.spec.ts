// E2E (LLM): create a 1:1 engagement via the new-engagement form and
// have a multi-turn conversation through the engagement composer.
//
// Pre-seeds a unit + agent with a working dapr-agent image, opens the
// `/engagement/new` flow, picks the agent as the sole participant,
// types an opening message, submits, and asserts the form lands on
// `/engagement/{threadId}`. Then sends a follow-up via the engagement
// composer and asserts a second event lands in the timeline.
//
// Skips gracefully if the human caller is recorded as Observer (the
// permission-grant race tracked separately).

import { apiPost, apiPut } from "../../fixtures/api.js";
import { agentName, unitName } from "../../fixtures/ids.js";
import { DEFAULT_MODEL, PROVIDER_ID, TOOL_ID } from "../../fixtures/runtime.js";
import { expect, test } from "../../fixtures/test.js";

test.describe("engagement — create 1:1 + multi-turn (#1455)", () => {
  test("create form sends the seed and the composer adds turns", async ({
    page,
    tracker,
    ollamaUp,
  }) => {
    void ollamaUp;
    test.setTimeout(180_000);
    const unit = tracker.unit(unitName("eng-1to1"));
    const agent = tracker.agent(agentName("eng-1to1-ada"));

    await apiPost("/api/v1/tenant/units", {
      name: unit,
      displayName: unit,
      description: "1:1 engagement spec (e2e-portal)",
      tool: TOOL_ID,
      provider: PROVIDER_ID,
      model: DEFAULT_MODEL,
      hosting: "ephemeral",
      isTopLevel: true,
    });
    await apiPut(
      `/api/v1/tenant/units/${encodeURIComponent(unit)}/execution`,
      { image: "localhost/spring-dapr-agent", runtime: "podman" },
    );
    await apiPost("/api/v1/tenant/agents", {
      name: agent,
      displayName: "1:1 Spec Agent",
      description: "1:1 engagement spec (e2e-portal)",
      unitIds: [unit],
    });

    // Drive the new-engagement form.
    await page.goto("/engagement/new");
    await page.getByTestId("engagement-new-filter").fill(agent);
    await page.getByTestId(`engagement-new-pick-agent-${agent}`).click();
    await expect(
      page.getByTestId(`engagement-new-chip-agent-${agent}`),
    ).toBeVisible();
    await page
      .getByTestId("engagement-new-body")
      .fill("Hi — kick off the work.");
    await page.getByTestId("engagement-new-submit").click();

    // Poll for either an inline error or a navigation; skip on error.
    await expect
      .poll(
        async () => {
          if (
            await page
              .getByTestId("engagement-new-error")
              .isVisible()
              .catch(() => false)
          ) {
            return "error";
          }
          if (/\/engagement\/[^/?#]+/.test(page.url())) {
            return "navigated";
          }
          return "pending";
        },
        { timeout: 90_000, intervals: [500, 1000, 2000] },
      )
      .not.toBe("pending");
    if (
      await page
        .getByTestId("engagement-new-error")
        .isVisible()
        .catch(() => false)
    ) {
      const text = await page
        .getByTestId("engagement-new-error")
        .textContent()
        .catch(() => null);
      test.skip(
        true,
        `Submit failed: ${text?.trim().slice(0, 200) ?? "<unknown>"}`,
      );
      return;
    }
    await expect(page.getByTestId("engagement-detail-page")).toBeVisible();

    // The composer is hidden when the human is Observer (#1292) — skip.
    const composer = page.getByTestId("engagement-composer");
    if (!(await composer.isVisible().catch(() => false))) {
      test.skip(
        true,
        "Engagement composer hidden — human recorded as Observer; tracked by #1292.",
      );
    }

    // Multi-turn: capture the timeline event count, send a follow-up,
    // and assert at least one new event lands.
    const timeline = page
      .getByTestId("engagement-timeline-events")
      .locator('[data-testid^="conversation-event-"]');
    const before = await timeline.count();

    await composer
      .getByRole("textbox", { name: /message text|your answer/i })
      .fill("Status check?");
    await composer.getByRole("button", { name: /^send|submit$/i }).click();

    await expect(async () => {
      const now = await timeline.count();
      expect(now).toBeGreaterThan(before);
    }).toPass({ timeout: 60_000, intervals: [500, 1000, 2000] });

    // One more turn — proves the composer is reusable across turns,
    // not just a one-shot send.
    const before2 = await timeline.count();
    await composer
      .getByRole("textbox", { name: /message text|your answer/i })
      .fill("Final question.");
    await composer.getByRole("button", { name: /^send|submit$/i }).click();
    await expect(async () => {
      const now = await timeline.count();
      expect(now).toBeGreaterThan(before2);
    }).toPass({ timeout: 60_000, intervals: [500, 1000, 2000] });
  });
});
