import { expect, test } from "../../fixtures/test.js";

/**
 * Wizard: Catalog source branch — package WITH required inputs (#1615).
 *
 * The companion file `04-units-create-from-template.spec.ts` covers the
 * two zero-input catalog packages (`software-engineering`,
 * `product-management`). It used to skip `spring-voyage-oss` because
 * that package declares three required GitHub inputs and the wizard's
 * pre-#1615 package step had no UI to collect them — the install would
 * 400 with `Input 'github_owner' is required`. PR #1616 papered over
 * the gap with an install-time auto-derive shim. #1615 fixes the root
 * cause: PackageDetail surfaces the input schema, the wizard renders a
 * typed field per declared input, the GitHub connector step pre-fills
 * the conventional GitHub keys, and required inputs without a value
 * AND no default block Next with an actionable in-form error.
 *
 * This spec drives `spring-voyage-oss` through the wizard with dummy
 * GitHub coordinates and asserts the install reaches the active state
 * (the navigation to `/units` after install is the wizard's
 * authoritative success signal — same pattern as the sibling spec).
 */

test.describe("units — create from package with inputs (catalog wizard)", () => {
  test("spring-voyage-oss package: typing inputs through the wizard reaches active", async ({
    page,
    tracker,
  }) => {
    // The OSS package's root unit is `spring-voyage-oss`; deleting it
    // with `?recursive=true` (the tracker's default) cascades through
    // the four sub-units the manifest declares.
    tracker.unit("spring-voyage-oss");
    tracker.unit("sv-oss-software-engineering");
    tracker.unit("sv-oss-design");
    tracker.unit("sv-oss-product-management");
    tracker.unit("sv-oss-program-management");

    await page.goto("/units/create");

    // Step 1 — Source: pick Catalog.
    await page.getByTestId("source-card-catalog").click();
    await page.getByRole("button", { name: /^next$/i }).click();

    // Step 2 — Package picker. Select spring-voyage-oss; the inputs
    // panel renders one field per declared input (#1615).
    await page
      .getByTestId(`package-option-spring-voyage-oss`)
      .waitFor({ timeout: 30_000 });
    await page.getByTestId(`package-option-spring-voyage-oss`).click();

    // Wait for the inputs panel to render — the schema fetch is
    // gated on package selection.
    await page.getByTestId("catalog-inputs").waitFor({ timeout: 15_000 });

    // The package declares github_owner / github_repo /
    // github_installation_id — all required, no defaults. Clicking
    // Next without filling them must surface the per-field "required"
    // hint and keep the wizard on the package step.
    await page.getByRole("button", { name: /^next$/i }).click();
    await expect(
      page.getByTestId("catalog-input-github_owner-missing"),
    ).toBeVisible();
    await expect(
      page.getByTestId("catalog-input-github_repo-missing"),
    ).toBeVisible();
    await expect(
      page.getByTestId("catalog-input-github_installation_id-missing"),
    ).toBeVisible();

    // Fill the dummy GitHub coordinates the CLI scenario uses
    // (`tests/e2e/cli/scenarios/packages/package-install-spring-voyage-oss.sh`).
    await page
      .getByTestId("catalog-input-github_owner-control")
      .fill("acme");
    await page
      .getByTestId("catalog-input-github_repo-control")
      .fill("demo");
    await page
      .getByTestId("catalog-input-github_installation_id-control")
      .fill("999");

    // The "missing" hints clear once values are present.
    await expect(
      page.getByTestId("catalog-input-github_owner-missing"),
    ).toHaveCount(0);

    await page.getByRole("button", { name: /^next$/i }).click();

    // Step 3 — Connector (skip; the OSS package itself does not require
    // a wizard-level GitHub binding — the connector is provisioned by
    // the package's own connector manifests at install time).
    const skip = page
      .getByRole("button", { name: /skip connector|don.?t bind/i })
      .first();
    if (await skip.isVisible().catch(() => false)) {
      await skip.click();
    } else {
      await page.getByRole("button", { name: /^next$/i }).click();
    }

    // Step 4 — Install. Same pattern as the sibling spec — the
    // wizard's `installActive` effect navigates to `/units` once the
    // install reaches the active terminal state.
    await page.getByTestId("install-unit-button").click();
    try {
      await page.waitForURL(
        (url) => !url.pathname.endsWith("/units/create"),
        { timeout: 90_000 },
      );
    } catch (err) {
      const failed = page.getByTestId("install-status-failed");
      if (await failed.isVisible().catch(() => false)) {
        const errText =
          (await failed.innerText().catch(() => "")) || "(no error text)";
        throw new Error(`Catalog install failed: ${errText}`);
      }
      throw err;
    }

    await expect(page).toHaveURL(new RegExp(`/units(?:\\?|$)`));
  });
});
