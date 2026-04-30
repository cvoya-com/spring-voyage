import type { Page } from "@playwright/test";

import { expect } from "@playwright/test";

/**
 * Engagement-portal helpers.
 *
 * Layout (E2.3+):
 *   /engagement              — redirects to /engagement/mine
 *   /engagement/mine         — "my engagements" list
 *   /engagement/<id>         — engagement detail (timeline + composer)
 */

export async function openMyEngagements(page: Page): Promise<void> {
  await page.goto("/engagement/mine");
  await expect(page.getByTestId("my-engagements-page")).toBeVisible();
}

export async function openEngagement(page: Page, threadId: string): Promise<void> {
  await page.goto(`/engagement/${encodeURIComponent(threadId)}`);
  await expect(page.getByTestId("engagement-detail-page")).toBeVisible();
}

/** Send a message into an engagement via the composer. */
export async function sendEngagementMessage(
  page: Page,
  body: string,
): Promise<void> {
  const composer = page.getByTestId("engagement-composer");
  await expect(composer).toBeVisible();
  // Composer textarea is the primary input inside the composer container.
  const input = composer.getByRole("textbox").first();
  await input.fill(body);
  await composer.getByRole("button", { name: /^send|submit$/i }).click();
}

/** Click the "Answer" call-to-action that surfaces when an agent asks the user a question. */
export async function clickAnswerCta(page: Page): Promise<void> {
  await page.getByTestId("engagement-question-cta").click();
}

/** Returns the count of timeline events currently rendered. */
export async function timelineEventCount(page: Page): Promise<number> {
  return page
    .getByTestId("engagement-timeline-events")
    .locator('[data-testid^="conversation-event-"]')
    .count();
}
