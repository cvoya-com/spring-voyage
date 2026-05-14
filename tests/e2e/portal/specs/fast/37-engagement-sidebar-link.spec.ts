// E2E: Engagement nav link in the management sidebar (#1454).
//
// The sidebar's Orchestrate cluster carries a new entry — Engagement,
// labelled `(experimental)` in a smaller font — sitting directly below
// Discovery. Clicking it lands on `/engagement` (which redirects to
// `/engagement/mine`).

import { expect, test } from "../../fixtures/test.js";

test.describe("portal sidebar — Engagement link (#1454)", () => {
  test("renders under Orchestrate, below Discovery, with the experimental sub-label", async ({
    page,
  }) => {
    await page.goto("/");
    const link = page.locator(
      '[data-testid="sidebar-nav-link-/engagement"]:visible',
    );
    await expect(link).toBeVisible();
    // The subordinate `(experimental)` label is testid'd separately so
    // the scaffolding doesn't depend on the exact rendered text.
    await expect(
      link.locator('[data-testid="sidebar-nav-link-/engagement-secondary"]'),
    ).toContainText(/experimental/i);
  });

  test("is positioned immediately below Discovery in the sidebar", async ({
    page,
  }) => {
    await page.goto("/");
    // Order is determined by `orderHint`; the registry test asserts
    // adjacency, but we also want a UI-side check: the engagement
    // link must follow the discovery link in DOM order.
    const ordered = await page.evaluate(() => {
      const els = Array.from(
        document.querySelectorAll<HTMLElement>(
          'aside[aria-label="Sidebar navigation"]:not(#mobile-sidebar) [data-testid^="sidebar-nav-link-/"]',
        ),
      );
      return els.map((el) => el.dataset.testid ?? "");
    });
    const discoveryIdx = ordered.indexOf("sidebar-nav-link-/discovery");
    const engagementIdx = ordered.indexOf("sidebar-nav-link-/engagement");
    expect(discoveryIdx).toBeGreaterThanOrEqual(0);
    expect(engagementIdx).toBe(discoveryIdx + 1);
  });

  test("navigates to /engagement on click and the active state highlights it", async ({
    page,
  }) => {
    await page.goto("/");
    await page
      .locator('[data-testid="sidebar-nav-link-/engagement"]:visible')
      .first()
      .click();
    // /engagement redirects to /engagement/mine (server redirect).
    await expect(page).toHaveURL(/\/engagement\/mine\b/);
    // Active state: aria-current="page" or visible indicator.
    await expect(
      page
        .locator('[data-testid="sidebar-nav-link-/engagement"]:visible')
        .first(),
    ).toHaveAttribute("aria-current", "page");
  });
});
