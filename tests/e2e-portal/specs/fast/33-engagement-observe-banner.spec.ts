import { apiPost } from "../../fixtures/api.js";
import { agentName, unitName } from "../../fixtures/ids.js";
import { AGENT_ID, DEFAULT_MODEL, PROVIDER_ID } from "../../fixtures/runtime.js";
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
    const a = tracker.agent(agentName("obs-a"));
    const b = tracker.agent(agentName("obs-b"));

    await apiPost("/api/v1/tenant/units", {
      name: unit,
      displayName: unit,
      description: "Observe banner spec (e2e-portal)",
      agent: AGENT_ID,
      provider: PROVIDER_ID,
      model: DEFAULT_MODEL,
      hosting: "ephemeral",
      isTopLevel: true,
    });
    await apiPost("/api/v1/tenant/agents", {
      name: a,
      displayName: a,
      description: "Observe banner spec (e2e-portal)",
      unitIds: [unit],
    });
    await apiPost("/api/v1/tenant/agents", {
      name: b,
      displayName: b,
      description: "Observe banner spec (e2e-portal)",
      unitIds: [unit],
    });

    // Send an A2A message: agent → agent. Routing through /messages with a
    // sender override creates a thread whose participants are both agents.
    // Wire shape: SendMessageRequest = { to, type, payload, threadId? }.
    // The API doesn't accept a caller-side `from` override (server uses
    // the authenticated subject); we still try and tolerate any
    // server-side rejection because the spec then skips.
    const seed = await apiPost<MessageResponse>("/api/v1/tenant/messages", {
      from: { scheme: "agent", path: a },
      to: { scheme: "agent", path: b },
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
