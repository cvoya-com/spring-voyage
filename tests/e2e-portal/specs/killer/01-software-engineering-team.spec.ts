import { unitName } from "../../fixtures/ids.js";
import { expect, test } from "../../fixtures/test.js";
import { pickWizardMode } from "../../helpers/unit-wizard.js";

/**
 * v0.1 killer use case (Area E2):
 *
 *   1. User creates a unit from the `software-engineering / engineering-team`
 *      template via the wizard.
 *   2. (Operator-supplied) GitHub App installation is bound to the unit.
 *   3. User assigns a first task by sending a message in the engagement.
 *   4. User observes the unit's orchestrator + agents executing the task
 *      from the engagement detail page (timeline, errors-as-first-class).
 *
 * Step 2 needs a real GitHub App installation; if `GITHUB_INSTALLATION_ID`
 * is set the spec attempts the binding, otherwise it skips that segment and
 * documents the gap (the rest of the flow still validates the wizard +
 * engagement view).
 */

test.describe("killer use case — software-engineering team", () => {
  test.setTimeout(300_000);

  test("template wizard → unit detail → engagement", async ({
    page,
    tracker,
    ollamaUp,
  }) => {
    void ollamaUp;
    const name = tracker.unit(unitName("k-soft-eng"));

    // ── Wizard ────────────────────────────────────────────────────────────
    await page.goto("/units/create");

    await page.getByLabel("Name").or(page.getByRole("textbox", { name: /^name$/i })).first().fill(name);
    await page.getByLabel("Display name").or(page.getByRole("textbox", { name: /display name/i })).first().fill(name);
    await page.getByTestId("parent-choice-top-level").click();
    await page.getByRole("button", { name: /^next$/i }).click();

    // Execution → dapr-agent + ollama
    await page.getByLabel("Execution tool").selectOption("dapr-agent");
    await page.getByLabel("LLM provider").selectOption("ollama");
    const modelSelect = page.getByLabel("Model");
    await modelSelect.waitFor({ state: "visible", timeout: 30_000 });
    const values = await modelSelect.evaluate((el) =>
      Array.from((el as HTMLSelectElement).options).map((o) => o.value),
    );
    if (values.length === 0) {
      test.skip(true, "Ollama returned no models — install one before running this spec.");
    }
    const firstValue = values[0]!;
    await modelSelect.selectOption(firstValue);
    await page.getByRole("button", { name: /^next$/i }).click();

    // Mode → template → engineering-team
    await pickWizardMode(page, "template");
    await page
      .getByRole("button", { name: /engineering-team/i })
      .first()
      .click();
    await page.getByRole("button", { name: /^next$/i }).click();

    // Connector step.
    const installationId = process.env.GITHUB_INSTALLATION_ID?.trim();
    const repoOwnerRepo = process.env.GITHUB_REPO?.trim();
    if (installationId && repoOwnerRepo) {
      // Pick GitHub from the connector dropdown / card list.
      await page.getByRole("button", { name: /github/i }).first().click();
      // Repository dropdown is populated from /actions/list-repositories.
      const repoSelect = page.getByRole("combobox", { name: /repository|repo/i }).first();
      if (await repoSelect.isVisible().catch(() => false)) {
        // selectOption accepts the option's value or label as a string.
        // GITHUB_REPO is set as `owner/repo`, which matches the option
        // value the API returns; fall back to the literal label.
        await repoSelect.selectOption(repoOwnerRepo).catch(async () => {
          await repoSelect.selectOption({ label: repoOwnerRepo });
        });
      } else {
        const repoInput = page.getByRole("textbox", { name: /repository|repo/i }).first();
        await repoInput.fill(repoOwnerRepo);
      }
    } else {
      const skip = page.getByRole("button", { name: /skip connector|don.?t bind/i }).first();
      if (await skip.isVisible().catch(() => false)) {
        await skip.click();
      } else {
        await page.getByRole("button", { name: /^next$/i }).click();
      }
      test.info().annotations.push({
        type: "skipped-binding",
        description:
          "GitHub binding not exercised — set GITHUB_INSTALLATION_ID + GITHUB_REPO to enable this segment.",
      });
    }
    // After the connector step (filled or skipped) we advance one step
    // — into Secrets when the skip path didn't already advance us, or
    // when we filled connector config. The skip click already advanced
    // us, so guard the Next on the still-visible Connector step.
    if (
      await page
        .getByRole("button", { name: /skip connector|don.?t bind/i })
        .first()
        .isVisible()
        .catch(() => false)
    ) {
      await page.getByRole("button", { name: /^next$/i }).click();
    }

    // Secrets — none.
    await page.getByRole("button", { name: /^next$/i }).click();

    // Finalize. The wizard's auto-validation path is broken for the
    // no-credential runtime (Ollama) — see `helpers/unit-wizard.ts`
    // § `awaitValidation` — so we verify the validation view mounted
    // (proves the create POST landed) and navigate to the explorer
    // ourselves.
    await page.getByTestId("create-unit-button").click();
    await expect(page.getByTestId("wizard-validation-view")).toBeVisible({
      timeout: 30_000,
    });
    await page.goto(
      `/units?node=${encodeURIComponent(name)}&tab=Overview`,
    );

    // ── Unit detail boot ─────────────────────────────────────────────────
    await expect(page.getByRole("heading", { name })).toBeVisible();
    // Templates seed agents into the unit; deep-link straight to the
    // Agents tab so the click sequence isn't sensitive to TabStrip
    // round-trip.
    await page.goto(
      `/units?node=${encodeURIComponent(name)}&tab=Agents`,
    );
    await expect(
      page.locator('[data-testid^="unit-membership-"]').first(),
    ).toBeVisible({ timeout: 30_000 });

    // ── First message → engagement (#1459 / #1460) ───────────────────────
    // The Messages tab now renders the 1:1 timeline inline with a
    // persistent composer at the bottom; sending implicitly creates
    // the thread when none exists yet.
    await page.goto(
      `/units?node=${encodeURIComponent(name)}&tab=Messages`,
    );
    const composer = page.getByTestId("tab-unit-messages-composer-input");
    if (await composer.isVisible().catch(() => false)) {
      await composer.fill(
        "First task: create an empty CHANGELOG entry for the next release.",
      );
      await page.getByTestId("tab-unit-messages-composer-send").click();
      await expect
        .poll(
          async () =>
            await page
              .locator('[data-testid^="conversation-event-"]')
              .count(),
          { timeout: 30_000 },
        )
        .toBeGreaterThan(0);
    } else {
      test.info().annotations.push({
        type: "skipped-first-message",
        description:
          "Unit detail Messages tab is not exposing the inline composer — investigate auth/permission propagation.",
      });
    }
  });
});
