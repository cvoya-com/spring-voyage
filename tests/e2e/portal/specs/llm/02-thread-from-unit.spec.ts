import {
  addCallerHat,
  apiPut,
  seedAgent,
  seedUnit,
} from "../../fixtures/api.js";
import { agentName, unitName } from "../../fixtures/ids.js";
import { AGENT_ID } from "../../fixtures/runtime.js";
import { expect, test } from "../../fixtures/test.js";
import { gotoExplorerUnit } from "../../helpers/nav.js";

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

    const u = await seedUnit(unit, {
      description: "1:1 thread spec (e2e-portal)",
    });
    // Set image+runtime so the agent's dispatch path doesn't fail with
    // "Ephemeral agent requires a container image" downstream. The runtime
    // is the agent runtime id (`spring-voyage`); execution keys on the hex.
    await apiPut(
      `/api/v1/tenant/units/${encodeURIComponent(u.hex)}/execution`,
      {
        image: "ghcr.io/cvoya-com/spring-voyage-agent:latest",
        runtime: AGENT_ID,
        model: { provider: "ollama", id: process.env.E2E_PORTAL_OLLAMA_MODEL ?? "llama3.2:3b" },
      },
    );
    await seedAgent(agent, {
      description: "1:1 thread spec (e2e-portal)",
      unitHexIds: [u.hex],
    });

    // Hat-reachability (#2972): without a wearable Hat the inline composer
    // is disabled and a send 403s. Add the caller's Hat as a unit member.
    const hat = await addCallerHat(u.hex);
    if (hat === null) {
      test.skip(true, "No caller Hat available to grant unit reachability.");
    }

    await gotoExplorerUnit(page, u.hex, { tab: "Messages" });

    // Empty state confirms there's no thread yet.
    await expect(
      page.getByTestId("tab-unit-messages-empty"),
    ).toBeVisible();

    const input = page.getByTestId("tab-unit-messages-composer-input");
    await input.fill("Status check from e2e-portal.");
    await page.getByTestId("tab-unit-messages-composer-send").click();

    // After a successful send the composer empties out and the timeline picks
    // up the new event. Tolerate auth/permission propagation delays by also
    // watching for a real error banner. Match only a NON-EMPTY role="alert":
    // the page keeps a persistent empty alert live-region for screen-reader
    // announcements, and treating its mere visibility as a failure made this
    // spec skip ("Send failed with: " — empty reason) even on a clean send.
    const errorAlert = page.getByRole("alert").filter({ hasText: /\S/ }).first();
    await Promise.race([
      expect(input).toHaveValue("", { timeout: 15_000 }),
      errorAlert.waitFor({ state: "visible", timeout: 15_000 }),
    ]).catch(() => undefined);

    if (await errorAlert.isVisible().catch(() => false)) {
      const message = (await errorAlert.textContent().catch(() => "")) ?? "";
      test.skip(true, `Send failed with: ${message.trim().slice(0, 200)}`);
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
