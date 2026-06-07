// E2E: AgentCreateForm — multi-parent inheritance conflict (ADR-0039 §6 / I6).
//
// The portal renders an inline error block when the direct create endpoint
// returns the structured 422 `MultiParentInheritanceConflict` body.
// Reproducing the conflict against the live backend requires at least two
// parent units with diverging execution config, so these specs mock only
// `POST /api/v1/tenant/agents` and exercise the form's detect-parse-render
// path end-to-end without needing the resolver to actually trigger.
//
// Cleanup: each spec registers the unit and agent names with the shared
// tracker. Only the successful resubmit creates an agent row.

import { seedUnit } from "../../fixtures/api.js";
import { agentName, unitName } from "../../fixtures/ids.js";
import { expect, test } from "../../fixtures/test.js";
import { openScratchAgentCreate } from "../../helpers/agent-create.js";

test.describe("agents — multi-parent inheritance conflict (ADR-0039 I6)", () => {
  test("renders the inline conflict block when direct create returns 422", async ({
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
    await seedUnit(unit, {
      description: "Multi-parent conflict spec (e2e-portal)",
    });

    // Mock just the direct create endpoint. The body shape and
    // conflict rendering are the load-bearing path for this spec.
    await page.route(
      (url) => url.pathname === "/api/v1/tenant/agents",
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

    await openScratchAgentCreate(page);

    // Agents are identified by display name only (no "Agent id" field).
    await page.getByLabel("Display name").fill(aId);

    await page
      .getByRole("checkbox", { name: new RegExp(`assign to ${unit}`, "i") })
      .first()
      .check();

    // Submit. The mocked create endpoint returns 422 and the form
    // renders the inline conflict block.
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

  test("submit re-enables after operator resolves conflict", async ({
    page,
    tracker,
  }) => {
    const unit = tracker.unit(unitName("agent-mp-resolve"));
    const aId = tracker.agent(agentName("mp-resolve"));

    // Seed one unit (same setup as the first test).
    await seedUnit(unit, {
      description: "Multi-parent resolve spec (e2e-portal)",
    });

    // First: wire the mock so the first direct-create submit returns
    // a 422 conflict. The second submit falls back to the live backend.
    let rejectNext = true;
    await page.route(
      (url) => url.pathname === "/api/v1/tenant/agents",
      async (route) => {
        if (route.request().method() !== "POST") {
          await route.fallback();
          return;
        }
        if (rejectNext) {
          rejectNext = false;
          await route.fulfill({
            status: 422,
            contentType: "application/problem+json",
            body: JSON.stringify({
              type: "https://docs.cvoya.com/spring/errors/multi-parent-inheritance-conflict",
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
        } else {
          await route.fallback();
        }
      },
    );

    await openScratchAgentCreate(page);
    await page.getByLabel("Display name").fill(aId);

    // Assign the unit to trigger the conflict path.
    await page
      .getByRole("checkbox", { name: new RegExp(`assign to ${unit}`, "i") })
      .first()
      .check();

    // First submit — gets the mocked 422.
    await page.getByTestId("agent-create-submit").click();
    const block = page.getByTestId("multi-parent-inheritance-conflict");
    await expect(block).toBeVisible({ timeout: 60_000 });
    await expect(page.getByTestId("agent-create-submit")).toBeDisabled();

    // Resolve: set an explicit value for the conflicting execution field. The
    // conflict block should clear and the submit button should re-enable.
    await page.getByLabel("Agent runtime").selectOption("claude-code");

    await expect(block).not.toBeVisible({ timeout: 10_000 });
    await expect(page.getByTestId("agent-create-submit")).toBeEnabled();
    await expect(page.getByTestId("execution-card-badge")).toHaveText(
      "Configured",
    );

    // Resubmit after correction. The second request falls back to the live
    // backend with the unit assignment still intact.
    await page.getByTestId("agent-create-submit").click();
    await page.waitForURL((url) => !url.pathname.endsWith("/agents/create"), {
      timeout: 60_000,
    });

    await page.goto("/agents");
    await expect(page.getByText(aId).first()).toBeVisible();
  });
});
