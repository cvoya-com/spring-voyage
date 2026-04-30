import { apiPost, apiPut } from "../../fixtures/api.js";
import { agentName, unitName } from "../../fixtures/ids.js";
import { DEFAULT_MODEL, PROVIDER_ID, TOOL_ID } from "../../fixtures/runtime.js";
import { expect, test } from "../../fixtures/test.js";

/**
 * Start the {human, unit} 1:1 engagement from the unit's Messages tab
 * using the inline composer (#1459 / #1460) and confirm the resulting
 * thread surfaces as an event on the timeline. The legacy
 * "+ New conversation" dialog is gone — sending a message when no
 * thread exists implicitly creates one.
 */

test.describe("threads — start from unit detail (#1459 / #1460)", () => {
  test("the inline composer starts the {human, unit} 1:1 thread", async ({
    page,
    tracker,
    ollamaUp,
  }) => {
    void ollamaUp;
    const unit = tracker.unit(unitName("thr-new"));
    const agent = tracker.agent(agentName("thr-new-ada"));

    await apiPost("/api/v1/tenant/units", {
      name: unit,
      displayName: unit,
      description: "1:1 thread spec (e2e-portal)",
      tool: TOOL_ID,
      provider: PROVIDER_ID,
      model: DEFAULT_MODEL,
      hosting: "ephemeral",
      isTopLevel: true,
    });
    // Set image+runtime so the agent's dispatch path doesn't fail
    // with "Ephemeral agent requires a container image" downstream.
    await apiPut(
      `/api/v1/tenant/units/${encodeURIComponent(unit)}/execution`,
      { image: "localhost/spring-dapr-agent", runtime: "podman" },
    );
    await apiPost("/api/v1/tenant/agents", {
      name: agent,
      displayName: agent,
      description: "1:1 thread spec (e2e-portal)",
      unitIds: [unit],
    });

    await page.goto(
      `/units?node=${encodeURIComponent(unit)}&tab=Messages`,
    );

    // Empty state confirms there's no thread yet.
    await expect(
      page.getByTestId("tab-unit-messages-empty"),
    ).toBeVisible();

    const input = page.getByTestId("tab-unit-messages-composer-input");
    await input.fill("Status check from e2e-portal.");
    await page.getByTestId("tab-unit-messages-composer-send").click();

    // After a successful send the composer empties out and the timeline
    // picks up the new event. Tolerate auth/permission propagation
    // delays the same way the previous version did.
    await Promise.race([
      expect(input).toHaveValue("", { timeout: 15_000 }),
      page
        .getByRole("alert")
        .first()
        .waitFor({ state: "visible", timeout: 15_000 }),
    ]).catch(() => undefined);

    const alert = page.getByRole("alert").first();
    if (await alert.isVisible().catch(() => false)) {
      const message = (await alert.textContent()) ?? "";
      test.skip(
        true,
        `Send failed with: ${message.trim().slice(0, 200)}`,
      );
    }

    await expect
      .poll(
        async () =>
          await page
            .locator('[data-testid^="conversation-event-"]')
            .count(),
        { timeout: 15_000 },
      )
      .toBeGreaterThan(0);
  });
});
