import { expect, test } from "../../fixtures/test.js";

/**
 * Wizard auth-choice sub-step (ADR-0047 §§ 6, 11). The GitHub connector
 * step on the new-unit wizard now surfaces two explicit auth branches —
 * "Use an App installation" and "Use a PAT secret". This spec exercises
 * both branches on the wizard UI without performing a real GitHub-side
 * round-trip (CI lacks an installed App / live OAuth flow); the
 * killer-use-case spec covers the live-binding path.
 *
 * The spec navigates through the scratch wizard branch up to the
 * Connector step, picks GitHub, and asserts:
 *   1. The App branch is the default choice; the qualified-repo input
 *      validates the `owner/repo` shape before letting the wizard pass.
 *   2. Flipping to the PAT branch surfaces the OAuth-authorize button
 *      AND the paste-a-secret-name input.
 */

test.describe("wizard — GitHub auth-choice sub-step (ADR-0047 §§ 6, 11)", () => {
  async function gotoConnectorStep(page: import("@playwright/test").Page) {
    await page.goto("/units/create");
    // Step 1 — Source: scratch.
    await page.getByTestId("source-card-scratch").click();
    await page.getByRole("button", { name: /^next$/i }).click();
    // Step 2 — Identity. Minimal name; top-level parent.
    await page
      .getByLabel("Name")
      .or(page.getByRole("textbox", { name: /^name$/i }))
      .first()
      .fill("wiz-github-auth-choice");
    await page
      .getByLabel("Display name")
      .or(page.getByRole("textbox", { name: /display name/i }))
      .first()
      .fill("Wizard GitHub auth-choice");
    await page.getByTestId("parent-choice-top-level").click();
    await page.getByRole("button", { name: /^next$/i }).click();
    // Step 3 — Execution. Take whatever model the wizard offered; the
    // catalog is dynamic on a dev/CI deployment.
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

    // Typing an unqualified value into the qualified-repo input must
    // surface the inline validation message.
    const qualifiedInput = page.getByTestId("github-repo-qualified");
    await qualifiedInput.fill("unqualified-repo-name");
    await expect(
      page.getByTestId("github-repo-validation"),
    ).toContainText(/owner\/repo/i);
  });

  test("PAT branch reveals the authorize button + paste-an-existing-secret-name input", async ({
    page,
  }) => {
    await gotoConnectorStep(page);

    const patRadio = page.getByTestId("github-auth-choice-pat");
    await expect(patRadio).toBeVisible({ timeout: 10_000 });
    await patRadio.click();
    await expect(patRadio).toBeChecked();

    await expect(
      page.getByTestId("github-pat-authorize"),
    ).toBeVisible();
    await expect(
      page.getByTestId("github-pat-secret-name"),
    ).toBeVisible();

    // Flipping back to App must hide the PAT inputs again.
    const appRadio = page.getByTestId("github-auth-choice-app");
    await appRadio.click();
    await expect(appRadio).toBeChecked();
    await expect(
      page.getByTestId("github-pat-authorize"),
    ).not.toBeVisible();
  });
});
