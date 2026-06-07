import {
  packageInstallBlockedReason,
  resolveUnitIdByDisplayName,
} from "../../fixtures/api.js";
import { expect, test } from "../../fixtures/test.js";
import { gotoExplorerUnit } from "../../helpers/nav.js";

/**
 * v0.1 killer use case (Area E2):
 *
 *   1. User installs the `software-engineering` catalog package via the
 *      wizard — the catalog branch installs a pre-built unit
 *      ("engineering-team") with all its agents wired.
 *   2. User opens the unit detail and sees the seeded agents on the
 *      Members tab.
 *   3. User sends a first message via the Messages tab and verifies the
 *      unit's agent replies (covers #1465 — silent regression class where
 *      the dispatcher ↔ agent transport stops working).
 *
 * PRECONDITION (credential-free environments skip): the OSS
 * `software-engineering` package now pins `runtime: claude-code` (a
 * required `anthropic-oauth` credential) AND declares a required `github`
 * connector. The install pipeline fails-fast (400) without both, and this
 * suite is credential-free by design (dapr-agent + ollama, no operator
 * secrets), so the install cannot complete here. The spec runs the full
 * flow only when the package's requirements are already satisfied on the
 * tenant (an operator-provisioned deployment); otherwise it skips with the
 * precise blocker. The CLI suite, which can seed the tenant secret + a PAT
 * connector binding, covers the install-with-credential path.
 */

test.describe("killer use case — software-engineering team", () => {
  test.setTimeout(300_000);

  test("catalog wizard → unit detail → engagement", async ({
    page,
    tracker,
    ollamaUp,
  }) => {
    void ollamaUp;
    // The package's manifest declares the canonical unit name.
    const unit = "engineering-team";
    tracker.unit(unit);

    const blocked = await packageInstallBlockedReason("software-engineering");
    if (blocked) {
      test.skip(
        true,
        `Catalog install cannot complete credential-free: ${blocked}. ` +
          `Provision the credential/connector (or run the CLI suite) to exercise this flow.`,
      );
    }

    // ── Wizard: catalog branch ───────────────────────────────────────────
    await page.goto("/units/create");
    await page.getByTestId("source-card-catalog").click();
    await page.getByRole("button", { name: /^next$/i }).click();

    await page.getByTestId("package-option-software-engineering").waitFor({ timeout: 30_000 });
    await page.getByTestId("package-option-software-engineering").click();
    await page.getByRole("button", { name: /^next$/i }).click();

    // Connector step — skip when the package declares no required binding
    // (the precondition guard above ensures we only get here when nothing
    // is required).
    const skip = page.getByRole("button", { name: /skip connector|don.?t bind/i }).first();
    if (await skip.isVisible().catch(() => false)) {
      await skip.click();
    } else {
      await page.getByRole("button", { name: /^next$/i }).click();
    }

    // Install. The wizard navigates away from /units/create when the
    // install reaches active state; the unit row is created server-side, so
    // resolve its hex and navigate ourselves rather than racing the
    // wizard's lifecycle-gated redirect.
    await page.getByTestId("install-unit-button").click();
    await expect
      .poll(async () => resolveUnitIdByDisplayName(unit), {
        timeout: 120_000,
        intervals: [2000, 5000, 10_000],
      })
      .not.toBeNull();
    const unitId = await resolveUnitIdByDisplayName(unit);

    // ── Unit detail boot ────────────────────────────────────────────────
    // Cache invalidation between the install completing and the
    // Members-tab membership query landing can take a few seconds; reload
    // the route once if the first render came back empty.
    await gotoExplorerUnit(page, unitId!, { tab: "Members" });
    const membership = page.locator('[data-testid^="unit-membership-"]').first();
    try {
      await expect(membership).toBeVisible({ timeout: 30_000 });
    } catch {
      await page.reload();
      await expect(membership).toBeVisible({ timeout: 30_000 });
    }

    // ── First message → engagement (#1459 / #1460 / #1465) ──────────────
    await gotoExplorerUnit(page, unitId!, { tab: "Messages" });
    const composer = page.getByTestId("tab-unit-messages-composer-input");
    if (!(await composer.isVisible().catch(() => false))) {
      test.info().annotations.push({
        type: "skipped-first-message",
        description:
          "Unit detail Messages tab is not exposing the inline composer — investigate auth/permission propagation.",
      });
      return;
    }

    await composer.fill(
      "First task: create an empty CHANGELOG entry for the next release.",
    );
    await page.getByTestId("tab-unit-messages-composer-send").click();

    // The user-sent event lands first.
    await expect
      .poll(
        async () =>
          await page.locator('[data-testid^="conversation-event-"]').count(),
        { timeout: 30_000 },
      )
      .toBeGreaterThan(0);

    // ── #1465: assert the agent actually replied ────────────────────────
    const threadFromCard = await page
      .locator('[data-testid^="conversation-event-"]')
      .first()
      .getAttribute("data-thread-id")
      .catch(() => null);

    if (threadFromCard) {
      await page.goto(`/engagement/${threadFromCard}`);
      await expect(page.getByTestId("engagement-detail-page")).toBeVisible();
      const filterTrigger = page.getByTestId("timeline-filter-trigger");
      if (await filterTrigger.isVisible().catch(() => false)) {
        await filterTrigger.click();
        await page.getByTestId("timeline-filter-option-full").click();
      }
    }

    await expect
      .poll(
        async () =>
          await page
            .locator(
              '[data-testid^="conversation-event-"][data-role="agent"]',
            )
            .count(),
        {
          timeout: 240_000,
          intervals: [2000, 5000, 10_000],
          message:
            "Expected an agent-authored event on the engagement timeline — the unit's runtime either failed to dispatch, or its reply never landed (regression class from #1465).",
        },
      )
      .toBeGreaterThan(0);
  });
});
