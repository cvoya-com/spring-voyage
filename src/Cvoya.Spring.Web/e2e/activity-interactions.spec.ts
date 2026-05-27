import { expect, test } from "@playwright/test";

/**
 * Boots-and-renders smoke test for the new
 * `/activity/interactions` route (#2867). Scope is the same as the
 * dashboard smoke spec — assert shell + chrome, not data-bound UI.
 * The Playwright web-server points at a non-routable upstream so the
 * snapshot endpoint returns nothing; we assert on:
 *
 *   - The route boots without uncaught client errors.
 *   - The filter card + page heading render.
 *   - The redirect from `/activity` → `/activity/events` works (the
 *     surface split is load-bearing for the sidebar nav).
 *   - The view-mode toggle exists with the expected default ("both").
 *   - The depth dial defaults to 2 hops.
 */

test.describe("activity interactions surface", () => {
  test("redirects /activity to /activity/events", async ({ page }) => {
    await page.goto("/activity");
    await expect(page).toHaveURL(/\/activity\/events$/);
  });

  test("/activity/interactions renders the filter strip and view-mode toggle", async ({
    page,
  }) => {
    const consoleErrors: string[] = [];
    page.on("pageerror", (err) => consoleErrors.push(err.message));
    page.on("console", (msg) => {
      if (msg.type() === "error") consoleErrors.push(msg.text());
    });

    await page.goto("/activity/interactions");

    // Page chrome rendered.
    await expect(page.getByTestId("interactions-page")).toBeVisible();
    await expect(
      page.getByRole("heading", { name: /interactions/i }),
    ).toBeVisible();

    // Filter strip present.
    await expect(page.getByTestId("interaction-filters")).toBeVisible();

    // Default depth dial = 2.
    await expect(
      page.getByTestId("interaction-filters-neighbours-2"),
    ).toHaveAttribute("aria-checked", "true");

    // Default view = "both".
    await expect(
      page.getByTestId("interaction-filters-view-both"),
    ).toHaveAttribute("aria-selected", "true");

    // No uncaught client errors during boot. Same allowlist as the
    // dashboard smoke spec — the upstream API is not routable in this
    // harness so fetch / 5xx logs are infra noise.
    const fatal = consoleErrors.filter(
      (msg) =>
        !msg.includes("Failed to fetch") &&
        !msg.includes("ERR_CONNECTION_REFUSED") &&
        !msg.includes("net::") &&
        !msg.toLowerCase().includes("fetch") &&
        !/Failed to load resource: the server responded with a status of (5\d\d|404|403)/i.test(
          msg,
        ),
    );
    expect(fatal, `unexpected client errors:\n${fatal.join("\n")}`).toEqual([]);
  });

  test("tab nav switches between Events and Interactions", async ({ page }) => {
    await page.goto("/activity/events");
    await expect(page.getByTestId("activity-tabs")).toBeVisible();

    await page.getByTestId("activity-tab-interactions").click();
    await expect(page).toHaveURL(/\/activity\/interactions$/);
    await expect(page.getByTestId("interactions-page")).toBeVisible();

    await page.getByTestId("activity-tab-events").click();
    await expect(page).toHaveURL(/\/activity\/events$/);
  });

  test("view-mode toggle to matrix updates the URL", async ({ page }) => {
    await page.goto("/activity/interactions");

    await page.getByTestId("interaction-filters-view-matrix").click();
    await expect(page).toHaveURL(/view=matrix/);
  });

  test("depth dial changing to 0 updates the URL", async ({ page }) => {
    await page.goto("/activity/interactions");
    await page.getByTestId("interaction-filters-neighbours-0").click();
    await expect(page).toHaveURL(/neighbours=0/);
  });

  /**
   * Rewind mode (#2872). The history endpoint is route-mocked so the
   * page has deterministic pulse data to replay; the snapshot fetch
   * stays at the non-routable default so live-mode pieces remain inert.
   */
  test.describe("rewind mode", () => {
    const SINCE = "2026-05-27T10:00:00.000Z";
    const UNTIL = "2026-05-27T10:10:00.000Z";

    async function mockHistory(page: import("@playwright/test").Page) {
      await page.route(
        "**/api/v1/tenant/observation/interactions/history*",
        async (route) => {
          await route.fulfill({
            status: 200,
            contentType: "application/json",
            body: JSON.stringify({
              nodes: [
                {
                  id: "agent-1",
                  kind: "agent",
                  displayName: "Agent",
                  sent: 5,
                  received: 2,
                },
                {
                  id: "unit-1",
                  kind: "unit",
                  displayName: "Unit",
                  sent: 2,
                  received: 5,
                },
              ],
              edges: [
                {
                  fromId: "agent-1",
                  toId: "unit-1",
                  count: 5,
                  firstAt: SINCE,
                  lastAt: UNTIL,
                  channels: ["unit"],
                },
              ],
              pulses: [
                {
                  messageId: "m1",
                  fromId: "agent-1",
                  toId: "unit-1",
                  timestamp: "2026-05-27T10:01:00.000Z",
                  threadId: null,
                  channel: "unit",
                },
                {
                  messageId: "m2",
                  fromId: "agent-1",
                  toId: "unit-1",
                  timestamp: "2026-05-27T10:05:00.000Z",
                  threadId: null,
                  channel: "unit",
                },
              ],
              truncated: null,
            }),
          });
        },
      );
    }

    test("deep link with rewind=true renders the transport bar", async ({
      page,
    }) => {
      await mockHistory(page);
      await page.goto(
        `/activity/interactions?rewind=true&since=${SINCE}&until=${UNTIL}`,
      );
      await expect(page.getByTestId("interaction-rewind")).toBeVisible();
      // Live indicator is absent.
      await expect(page.getByTestId("interaction-live-status")).toHaveCount(0);
      // Transport readout is mm:ss / mm:ss.
      await expect(page.getByTestId("interaction-rewind-elapsed")).toContainText(
        /\d\d:\d\d/,
      );
      await expect(page.getByTestId("interaction-rewind-total")).toContainText(
        "10:00",
      );
    });

    test("pressing play advances the elapsed readout; pause halts it", async ({
      page,
    }) => {
      await mockHistory(page);
      await page.goto(
        `/activity/interactions?rewind=true&since=${SINCE}&until=${UNTIL}`,
      );
      const elapsed = page.getByTestId("interaction-rewind-elapsed");
      await expect(elapsed).toBeVisible();
      // Crank speed to 1000× so a sub-second wait moves the readout
      // visibly past 00:00 — the default 30× still lands above 00:00
      // within ~30ms but the assertion is more reliable with a wider
      // jump.
      await page.getByTestId("interaction-rewind-speed-1000").click();
      await page.getByTestId("interaction-rewind-play").click();
      // Wait for the cursor to bump past the first 10-second tick.
      await expect(elapsed).not.toHaveText("00:00", { timeout: 5000 });

      // Pause and ensure the readout doesn't keep climbing.
      await page.getByTestId("interaction-rewind-play").click();
      const paused = await elapsed.textContent();
      // Give real-time long enough that an unpaused cursor would shift.
      await page.waitForTimeout(500);
      expect(await elapsed.textContent()).toBe(paused);
    });

    test("toggling rewind off restores live indicator and drops the bar", async ({
      page,
    }) => {
      await mockHistory(page);
      await page.goto(
        `/activity/interactions?rewind=true&since=${SINCE}&until=${UNTIL}`,
      );
      await expect(page.getByTestId("interaction-rewind")).toBeVisible();

      // Flip Live on — mutual exclusion forces rewind off.
      await page.getByTestId("interaction-filters-live-toggle").click();
      await expect(page.getByTestId("interaction-rewind")).toHaveCount(0);
      await expect(page).toHaveURL(/live=true/);
      await expect(page).not.toHaveURL(/rewind=true/);
    });

    test("rewind toggle in filters lights up the bar", async ({ page }) => {
      await mockHistory(page);
      await page.goto("/activity/interactions");
      await expect(
        page.getByTestId("interaction-filters-rewind-toggle"),
      ).toBeVisible();
      await page.getByTestId("interaction-filters-rewind-toggle").click();
      await expect(page).toHaveURL(/rewind=true/);
      await expect(page.getByTestId("interaction-rewind")).toBeVisible();
    });
  });
});
