import { AGENT_ID, DEFAULT_MODEL, PROVIDER_ID } from "../../fixtures/runtime.js";
import { expect, test } from "../../fixtures/test.js";

/**
 * Wizard auth-choice sub-step (ADR-0047 §§ 6, 11). The GitHub connector
 * step on the new-unit wizard surfaces two explicit auth branches —
 * "Use an App installation" and "Use a PAT secret"
 * (`src/Cvoya.Spring.Connector.GitHub/web/connector-wizard-step.tsx`).
 * This spec exercises both branches on the wizard UI without performing a
 * real GitHub-side round-trip (the dev stack lacks an installed App / live
 * OAuth); the killer-use-case spec covers the live-binding path.
 *
 * The spec navigates through the scratch wizard branch up to the
 * Connector step, picks GitHub, and asserts:
 *   1. The App branch is the default; the qualified-repo input validates
 *      the `owner/repo` shape.
 *   2. Flipping to the PAT branch surfaces the token input + save button;
 *      flipping back to App hides them.
 */

test.describe("wizard — GitHub auth-choice sub-step (ADR-0047 §§ 6, 11)", () => {
  async function gotoConnectorStep(page: import("@playwright/test").Page) {
    await page.goto("/units/create");
    // Step 1 — Source: scratch.
    await page.getByTestId("source-card-scratch").click();
    await page.getByRole("button", { name: /^next$/i }).click();
    // Step 2 — Identity. Minimal name; top-level parent.
    await page
      .getByRole("textbox", { name: /^name/i })
      .first()
      .fill("wiz-github-auth-choice");
    await page
      .getByRole("textbox", { name: /display name/i })
      .first()
      .fill("Wizard GitHub auth-choice");
    await page.getByTestId("parent-choice-top-level").click();
    await page.getByRole("button", { name: /^next$/i }).click();
    // Step 3 — Execution. Pin the credential-free runtime + a model so Next
    // un-gates (the wizard blocks Next until a model is selected).
    await page.getByTestId("unit-create-runtime-select").selectOption(AGENT_ID);
    await page
      .getByTestId("unit-create-model-provider-select")
      .selectOption(PROVIDER_ID);
    const modelSelect = page.getByTestId("unit-create-model-select");
    await modelSelect.waitFor({ state: "visible", timeout: 30_000 });
    const values = await modelSelect.evaluate((el) =>
      Array.from((el as HTMLSelectElement).options).map((o) => o.value),
    );
    if (values.length === 0) test.skip(true, "Ollama returned no models");
    await modelSelect.selectOption(
      values.includes(DEFAULT_MODEL) ? DEFAULT_MODEL : values[0]!,
    );
    await page.getByRole("button", { name: /^next$/i }).click();
    // Step 4 — Connector. Pick GitHub.
    const githubCard = page
      .getByRole("radio", { name: /github/i })
      .or(page.getByText(/github/i).first());
    await githubCard.first().click();
  }

  test("App branch is the default; rejects unqualified repo input", async ({
    page,
  }) => {
    await gotoConnectorStep(page);

    // The auth-choice fieldset must render with App selected by default.
    const appRadio = page.getByTestId("github-auth-choice-app");
    const patRadio = page.getByTestId("github-auth-choice-pat");
    await expect(appRadio).toBeVisible({ timeout: 10_000 });
    await expect(appRadio).toBeChecked();
    await expect(patRadio).not.toBeChecked();

    // The qualified-repo input only renders in App mode when the GitHub
    // App is configured AND an OAuth session exists (`disabledReason` /
    // `missingOAuth` both null). On a dev stack the App is registered but
    // no operator GitHub OAuth session exists, so the App branch shows the
    // "link your GitHub account" guidance (`github-link-account`) instead
    // of the repo dropdown. Assert the repo-validation when the input is
    // present; otherwise assert the App-branch credential guidance so the
    // branch isn't silently empty.
    const qualifiedInput = page.getByTestId("github-repo-qualified");
    if (await qualifiedInput.isVisible().catch(() => false)) {
      await qualifiedInput.fill("unqualified-repo-name");
      await expect(
        page.getByTestId("github-repo-validation"),
      ).toContainText(/owner\/repo/i);
    } else {
      await expect(
        page
          .getByTestId("github-link-account")
          .or(page.getByTestId("github-missing-oauth"))
          .first(),
      ).toBeVisible({ timeout: 10_000 });
    }
  });

  test("PAT branch reveals the token input + save button", async ({
    page,
  }) => {
    await gotoConnectorStep(page);

    const patRadio = page.getByTestId("github-auth-choice-pat");
    await expect(patRadio).toBeVisible({ timeout: 10_000 });
    await patRadio.click();
    await expect(patRadio).toBeChecked();

    // PAT branch surfaces the token input + "Use this token" save button
    // (the no-App-required path).
    await expect(page.getByTestId("github-pat-token")).toBeVisible();
    await expect(page.getByTestId("github-pat-save")).toBeVisible();

    // Flipping back to App must hide the PAT inputs again.
    const appRadio = page.getByTestId("github-auth-choice-app");
    await appRadio.click();
    await expect(appRadio).toBeChecked();
    await expect(page.getByTestId("github-pat-token")).not.toBeVisible();
  });
});
