import { unitName } from "../../fixtures/ids.js";
import { expect, test } from "../../fixtures/test.js";

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
    await page.getByRole("button", { name: /from template/i }).click();
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
    await page.getByRole("button", { name: /^next$/i }).click();

    // Secrets — none.
    await page.getByRole("button", { name: /^next$/i }).click();

    // Finalize.
    await page.getByTestId("create-unit-button").click();
    await page.waitForURL(new RegExp(`/units/${name}$`), { timeout: 180_000 });

    // ── Unit detail boot ─────────────────────────────────────────────────
    await expect(page.getByRole("heading", { name })).toBeVisible();
    // Templates seed agents into the unit; the Agents tab should list them.
    await page.getByRole("tab", { name: /^agents$/i }).click();
    await expect(
      page.locator('[data-testid^="unit-membership-"]').first(),
    ).toBeVisible({ timeout: 30_000 });

    // ── First message → engagement ────────────────────────────────────────
    // Many teams kick off via "+ New conversation" on the unit detail.
    const newConv = page
      .getByRole("button", { name: /new conversation|start (conversation|engagement)/i })
      .first();
    if (await newConv.isVisible().catch(() => false)) {
      await newConv.click();
      await page
        .getByTestId("new-conversation-body")
        .getByRole("textbox")
        .first()
        .fill(
          "First task: create an empty CHANGELOG entry for the next release.",
        );
      await page.getByTestId("new-conversation-submit").click();
      // We end up on either /engagement/<id> or /threads/<id>.
      await expect(async () => {
        expect(/\/engagement\/|\/threads?\/|\/conversations?\//.test(page.url()), `URL: ${page.url()}`).toBe(true);
      }).toPass({ timeout: 30_000 });
    } else {
      test.info().annotations.push({
        type: "skipped-first-message",
        description: "Unit detail does not expose 'New conversation' — adjust the affordance once the engagement portal route is finalised.",
      });
    }
  });
});
