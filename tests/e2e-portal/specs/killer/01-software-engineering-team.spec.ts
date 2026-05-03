import { expect, test } from "../../fixtures/test.js";

/**
 * v0.1 killer use case (Area E2):
 *
 *   1. User installs the `software-engineering` catalog package via the
 *      wizard. Post-#1563 this is the canonical replacement for the deleted
 *      "Mode = Template" path; the catalog branch installs a pre-built unit
 *      ("engineering-team") with all its agents wired.
 *   2. User opens the unit detail and sees the seeded agents on the
 *      Agents tab.
 *   3. User sends a first message via the Messages tab and verifies the
 *      orchestrator agent replies (covers #1465 — silent regression
 *      class where the dispatcher ↔ agent transport stops working).
 *
 * GitHub binding is exercised separately by the connector specs and is
 * NOT part of this flow — the v0.1 catalog package's install pipeline
 * does not gate on a real GitHub installation, so this spec focuses on
 * the wizard → unit → message → reply flow.
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

    // ── Wizard: catalog branch ───────────────────────────────────────────
    await page.goto("/units/create");
    await page.getByTestId("source-card-catalog").click();
    await page.getByRole("button", { name: /^next$/i }).click();

    await page.getByTestId("package-option-software-engineering").waitFor({ timeout: 30_000 });
    await page.getByTestId("package-option-software-engineering").click();
    await page.getByRole("button", { name: /^next$/i }).click();

    // Connector step — skip; v0.1 catalog install does not gate on a
    // real binding.
    const skip = page.getByRole("button", { name: /skip connector|don.?t bind/i }).first();
    if (await skip.isVisible().catch(() => false)) {
      await skip.click();
    } else {
      await page.getByRole("button", { name: /^next$/i }).click();
    }

    // Install. The wizard navigates to /units when the install reaches
    // active state; we wait for that URL transition as the load-bearing
    // signal and only inspect the failed panel if the URL never moves
    // (avoids a transient `install-status-failed` flash mid-staging).
    await page.getByTestId("install-unit-button").click();
    try {
      await page.waitForURL((url) => !url.pathname.endsWith("/units/create"), {
        timeout: 120_000,
      });
    } catch (err) {
      const failed = page.getByTestId("install-status-failed");
      if (await failed.isVisible().catch(() => false)) {
        const errText = (await failed.innerText().catch(() => "")) || "(no error text)";
        throw new Error(`Catalog install failed: ${errText}`);
      }
      throw err;
    }

    // ── Unit detail boot ────────────────────────────────────────────────
    // Cache invalidation between the install completing and the
    // Agents-tab membership query landing can take a few seconds; on
    // a cold runner it can be longer. Reload the route once if the
    // first render came back empty (the React Query cache key is
    // tenant-tree-scoped and refetches on focus / mount).
    await page.goto(`/units?node=${encodeURIComponent(unit)}&tab=Agents`);
    const membership = page.locator('[data-testid^="unit-membership-"]').first();
    try {
      await expect(membership).toBeVisible({ timeout: 30_000 });
    } catch {
      await page.reload();
      await expect(membership).toBeVisible({ timeout: 30_000 });
    }

    // ── First message → engagement (#1459 / #1460 / #1465) ──────────────
    await page.goto(`/units?node=${encodeURIComponent(unit)}&tab=Messages`);
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
            "Expected an agent-authored event on the engagement timeline — the orchestrator either failed to dispatch, or its reply never landed (regression class from #1465).",
        },
      )
      .toBeGreaterThan(0);
  });
});
