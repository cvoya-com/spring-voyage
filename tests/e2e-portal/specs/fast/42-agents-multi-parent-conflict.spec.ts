// E2E: AgentCreateForm — multi-parent inheritance conflict (ADR-0039 §6 / I6).
//
// The portal renders an inline error block when the membership-add
// endpoint returns the structured 422 `MultiParentInheritanceConflict`
// body. Reproducing the conflict against the live backend requires
// at least two parent units with diverging execution-config — that is
// expensive to set up here; instead we mock just the membership-add
// route via `page.route()` so the test exercises the form's
// detect-parse-render path end-to-end without needing the resolver
// to actually trigger.
//
// Cleanup: the install runs against the real backend, so the agent
// row that lands as part of Phase-1 must be tracked for `afterEach`
// teardown. The unit is also tracked; both are deleted by the
// shared tracker fixture.

import { apiPost } from "../../fixtures/api.js";
import { agentName, unitName } from "../../fixtures/ids.js";
import { AGENT_ID, DEFAULT_MODEL, PROVIDER_ID } from "../../fixtures/runtime.js";
import { expect, test } from "../../fixtures/test.js";

test.describe("agents — multi-parent inheritance conflict (ADR-0039 I6)", () => {
  test("renders the inline conflict block when membership-add returns 422", async ({
    page,
    tracker,
  }) => {
    const unit = tracker.unit(unitName("agent-mp-conflict"));
    const aId = tracker.agent(agentName("mp-conflict"));

    // Seed a single unit so the unit-assignment picker has at least
    // one row. Conflict semantics need two parents but we only need
    // one in the picker — the API response we fake names two
    // *different* parent unit ids, which is enough for the form to
    // render every diverging-value row.
    await apiPost("/api/v1/tenant/units", {
      name: unit,
      displayName: unit,
      description: "Multi-parent conflict spec (e2e-portal)",
      agent: AGENT_ID,
      provider: PROVIDER_ID,
      model: DEFAULT_MODEL,
      hosting: "ephemeral",
      isTopLevel: true,
    });

    // Mock just the membership-add endpoint. The install (Phase 1)
    // runs against the real backend so the operator-visible state
    // matches what they'd see in production. Pattern matches
    // `/api/v1/tenant/units/<unit>/agents/<agentId>` POST only.
    await page.route(
      (url) =>
        url.pathname.startsWith("/api/v1/tenant/units/") &&
        url.pathname.includes("/agents/"),
      async (route) => {
        if (route.request().method() !== "POST") {
          await route.fallback();
          return;
        }
        await route.fulfill({
          status: 422,
          contentType: "application/problem+json",
          body: JSON.stringify({
            type:
              "https://docs.cvoya.com/spring/errors/multi-parent-inheritance-conflict",
            title: "Multi-parent inheritance conflict",
            status: 422,
            detail:
              "Inherited execution-config fields disagree across parent units.",
            error: "MultiParentInheritanceConflict",
            conflictingFields: {
              runtime: [
                {
                  source: "00000000000000000000000000000001",
                  value: "claude-code",
                },
                {
                  source: "00000000000000000000000000000002",
                  value: "spring-voyage",
                },
              ],
            },
          }),
        });
      },
    );

    await page.goto("/agents/create");

    await page.getByLabel("Agent id").fill(aId);
    await page.getByLabel("Display name").fill("Multi Parent Conflict");

    await page
      .getByRole("checkbox", { name: new RegExp(`assign to ${unit}`, "i") })
      .first()
      .check();

    // Submit. The install completes against the real backend, then
    // the mocked membership-add returns 422 and the form renders the
    // inline conflict block.
    await page.getByTestId("agent-create-submit").click();

    const block = page.getByTestId("multi-parent-inheritance-conflict");
    await expect(block).toBeVisible({ timeout: 60_000 });
    await expect(block).toContainText("runtime");
    await expect(block).toContainText("claude-code");
    await expect(block).toContainText("spring-voyage");

    // Per-field card surfaces with the field-name testid suffix.
    await expect(
      page.getByTestId("multi-parent-inheritance-conflict-field-runtime"),
    ).toBeVisible();

    // Submit button is disabled while the conflict block is showing.
    await expect(page.getByTestId("agent-create-submit")).toBeDisabled();
  });
});
