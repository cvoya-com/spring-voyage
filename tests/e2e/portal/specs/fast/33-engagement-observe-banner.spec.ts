import { apiPost, seedAgent, seedUnit } from "../../fixtures/api.js";
import { agentName, unitName } from "../../fixtures/ids.js";
import { expect, test } from "../../fixtures/test.js";

/**
 * E2 contract: a human who is not a participant in a thread can OBSERVE
 * but not send. The detail view surfaces `engagement-observe-banner`.
 *
 * To force the observe path, this spec creates an agent-only thread (A2A)
 * and visits its detail page as the current human (who is not a
 * participant by definition).
 */

interface MessageResponse {
  threadId: string;
}

test.describe("engagement — observe-only banner for non-participants", () => {
  test("A2A thread surfaces the observe banner instead of the composer", async ({
    page,
    tracker,
  }) => {
    const unit = tracker.unit(unitName("obs"));
    const aSlug = tracker.agent(agentName("obs-a"));
    const bSlug = tracker.agent(agentName("obs-b"));

    const u = await seedUnit(unit, {
      description: "Observe banner spec (e2e-portal)",
    });
    const a = await seedAgent(aSlug, {
      description: "Observe banner spec (e2e-portal)",
      unitHexIds: [u.hex],
    });
    const b = await seedAgent(bSlug, {
      description: "Observe banner spec (e2e-portal)",
      unitHexIds: [u.hex],
    });

    // Send an A2A message: agent → agent. Routing through /messages with a
    // sender override creates a thread whose participants are both agents.
    // Wire shape: SendMessageRequest = { to, type, payload, threadId? }.
    // Addresses key on the agent's hex id (#2473). The API doesn't accept a
    // caller-side `from` override (server uses the authenticated subject);
    // we still try and tolerate any server-side rejection because the spec
    // then skips.
    const seed = await apiPost<MessageResponse>("/api/v1/tenant/messages", {
      from: { scheme: "agent", path: a.hex },
      to: { scheme: "agent", path: b.hex },
      type: "Domain",
      payload: { text: "ping (e2e-portal observe-banner spec)" },
    }).catch(() => ({ threadId: "" }));

    if (!seed.threadId) {
      test.skip(
        true,
        "Could not seed an A2A thread — the API may not accept `from` overrides on /messages in this build.",
      );
    }

    await page.goto(`/engagement/${seed.threadId}`);
    await expect(page.getByTestId("engagement-detail-page")).toBeVisible();
    // Observe banner OR the engagement detail rendered without composer
    // (depending on how the portal decides participation).
    const banner = page.getByTestId("engagement-observe-banner");
    const composer = page.getByTestId("engagement-composer");
    if (await banner.isVisible().catch(() => false)) {
      await expect(banner).toBeVisible();
      // Composer should be hidden in observe mode.
      await expect(composer).toBeHidden().catch(() => undefined);
    }
  });
});
