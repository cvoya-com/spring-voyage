import type { Page } from "@playwright/test";

/**
 * Drive `/agents/create` — see `src/Cvoya.Spring.Web/src/app/agents/create/page.tsx`.
 *
 * Uses aria-label selectors because the form (#744) leans on labelled
 * inputs rather than `data-testid` attributes for the primary fields.
 */

export interface AgentCreateOptions {
  id: string;
  displayName: string;
  role?: string;
  /** Names of units to assign at creation. The first becomes the derived primary. */
  unitNames: string[];
  /** Override the inherited agent runtime (rare). */
  runtime?: string;
  /** Optional model id. Inherits from unit when omitted. */
  model?: string;
}

/** Open `/agents/create` and advance the page-only Source step to Scratch. */
export async function openScratchAgentCreate(page: Page): Promise<void> {
  await page.goto("/agents/create");
  await page.getByTestId("agent-source-card-scratch").click();
  await page.getByRole("button", { name: /^next$/i }).click();
  await page.getByLabel("Agent id").waitFor();
}

/** Submit the form. Resolves when the page navigates away from `/agents/create`. */
export async function createAgent(page: Page, opts: AgentCreateOptions): Promise<void> {
  await openScratchAgentCreate(page);

  await page.getByLabel("Agent id").fill(opts.id);
  await page.getByLabel("Display name").fill(opts.displayName);
  if (opts.role) {
    await page.getByLabel("Role").fill(opts.role);
  }

  if (opts.runtime) {
    await page.getByLabel("Agent runtime").selectOption(opts.runtime);
  }

  if (opts.model) {
    // Optional — the dropdown can be empty depending on the runtime catalog.
    const modelSelect = page.getByLabel("Model");
    const values = await modelSelect.evaluate((el) =>
      Array.from((el as HTMLSelectElement).options).map((o) => o.value),
    );
    if (values.includes(opts.model)) {
      await modelSelect.selectOption(opts.model);
    }
  }

  for (const unit of opts.unitNames) {
    await page
      .getByRole("checkbox", { name: new RegExp(`assign to ${unit}`, "i") })
      .first()
      .check();
  }

  // Submit. Post-#1561 the page navigates to the units explorer's
  // Agents tab on success (`/units?node=<unit>&tab=Agents`), or to
  // `/units` when no unit was assigned. Wait for any navigation away
  // from `/agents/create`.
  await page.getByRole("button", { name: /^create agent$|^create$/i }).click();
  await page.waitForURL((url) => !url.pathname.endsWith("/agents/create"), {
    timeout: 60_000,
  });
}
