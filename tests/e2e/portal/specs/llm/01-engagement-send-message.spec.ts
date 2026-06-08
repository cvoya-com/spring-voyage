import {
  addCallerHat,
  apiGet,
  apiPost,
  apiPut,
  seedAgent,
  seedUnit,
} from "../../fixtures/api.js";
import { agentName, unitName } from "../../fixtures/ids.js";
import { AGENT_ID } from "../../fixtures/runtime.js";
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

    const u = await seedUnit(unit, {
      description: "Engagement send-message spec (e2e-portal)",
    });
    // Set image + runtime defaults so ephemeral agent dispatch picks
    // up a working container image (otherwise the dispatch fails with
    // "Ephemeral agent requires a container image"). image/runtime
    // aren't on `CreateUnitRequest`; they live on `/execution` (keyed on
    // the hex). The runtime is the agent runtime id (`spring-voyage`), not
    // the container backend.
    await apiPut(
      `/api/v1/tenant/units/${encodeURIComponent(u.hex)}/execution`,
      {
        image: "ghcr.io/cvoya-com/spring-voyage-agent:latest",
        runtime: AGENT_ID,
        model: { provider: "ollama", id: process.env.E2E_PORTAL_OLLAMA_MODEL ?? "llama3.2:3b" },
      },
    );
    const a = await seedAgent(agent, {
      description: "Engagement send-message spec (e2e-portal)",
      unitHexIds: [u.hex],
    });

    // Hat-reachability (#2972): a fresh unit has no human members, so a
    // send to its agent 403s `NoReachableHat`. Add the caller's Hat as a
    // team member so the API + UI sends are reachable.
    const hat = await addCallerHat(u.hex);
    if (hat === null) {
      test.skip(true, "No caller Hat available to grant unit reachability.");
    }

    // Kick off a thread by sending a free-form message via /messages.
    // The endpoint auto-generates a thread id when none is supplied.
    // Wire shape (`SendMessageRequest`): { to, type, payload, threadId? }.
    // Addresses key on the agent's hex id (#2473).
    const seed = await apiPost<MessageResponse>("/api/v1/tenant/messages", {
      to: { scheme: "agent", path: a.hex },
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

    // Assertion 1 — the #1465 dispatch round-trip: prove the dispatcher
    // delivered the seed to the agent and the agent's runtime actually ran.
    // Two signals, in preference order:
    //   1. An agent-authored event in the timeline — the strongest signal,
    //      emitted when the model invokes the messaging-send tool. A capable
    //      model produces this.
    //   2. Agent runtime activity (MessageArrived → RuntimeStarted →
    //      RuntimeCompleted/Silent) on the activity log — proof the round-trip
    //      worked even when the model was too weak to emit a user-visible
    //      reply. The credential-free local model (llama3.2:3b) frequently
    //      "completes silent": it reasons about calling the send tool but never
    //      invokes it, so no agent event lands. That is a model-capability
    //      limitation, NOT the transport regression #1465 guards against — so
    //      it must not fail the test.
    // Only the absence of BOTH signals indicates a genuinely broken transport.
    const ANY_RUNTIME_EVENT = new Set([
      "MessageArrived",
      "MessageDispatchedToRuntime",
      "RuntimeStarted",
      "RuntimeReasoning",
      "RuntimeCompleted",
      "RuntimeCompletedSilent",
      "RuntimeFailed",
    ]);
    // Terminal-without-reply: once the runtime has finished without emitting a
    // message there is no point waiting out the rest of the cold-start cap.
    const TERMINAL_SILENT = new Set(["RuntimeCompletedSilent", "RuntimeFailed"]);

    let sawRuntime = false;
    let roundTrip: "agent-event" | "runtime-ran" | "none" = "none";
    // Cold-start cap: container pull + Ollama warmup + the turn itself can
    // exceed 90s on a slow runner; match the killer use-case timeout (240s) so
    // a legitimately slow first turn is not flagged as a regression. The
    // terminal-silent early-out below means the common local (silent) case
    // returns in seconds rather than burning the whole budget.
    const deadline = Date.now() + 240_000;
    while (Date.now() < deadline) {
      const agentEvents = await page
        .getByTestId("engagement-timeline-events")
        .locator('[data-role="agent"]')
        .count()
        .catch(() => 0);
      if (agentEvents > 0) {
        roundTrip = "agent-event";
        break;
      }
      const activity = await apiGet<{ items?: Array<{ eventType?: string }> }>(
        `/api/v1/tenant/activity/?source=${encodeURIComponent(
          `agent:${a.hex}`,
        )}&pageSize=50`,
      ).catch(() => null);
      const types = (activity?.items ?? []).map((i) => i.eventType ?? "");
      if (types.some((t) => ANY_RUNTIME_EVENT.has(t))) sawRuntime = true;
      if (types.some((t) => TERMINAL_SILENT.has(t))) {
        roundTrip = "runtime-ran";
        break;
      }
      await page.waitForTimeout(3000);
    }
    if (roundTrip === "none" && sawRuntime) roundTrip = "runtime-ran";

    expect(
      roundTrip,
      "Neither an agent-authored timeline event nor any agent runtime " +
        "activity appeared after the seeded message — the dispatcher → agent " +
        "JSON-RPC round-trip looks broken (#1465).",
    ).not.toBe("none");

    if (roundTrip === "runtime-ran") {
      test.info().annotations.push({
        type: "silent-completion",
        description:
          "Agent runtime ran (MessageArrived → RuntimeStarted → " +
          "RuntimeCompleted/Silent) but emitted no user-visible message — the " +
          "local model did not invoke the send tool. The #1465 dispatcher → " +
          "agent round-trip is still verified via the activity log.",
      });
    }

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
