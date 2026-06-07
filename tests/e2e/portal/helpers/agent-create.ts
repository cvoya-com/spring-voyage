import type { Page } from "@playwright/test";

/**
 * Drive `/agents/create` — see `src/Cvoya.Spring.Web/src/app/agents/create/page.tsx`,
 * which renders the shared `<AgentCreateForm context="page">`
 * (`src/components/agents/create-form.tsx`).
 *
 * Identity model (#2473 / ADR-0039): agents no longer carry an
 * operator-supplied id/slug. The form collects `Display name`, optional
 * `Role`, runtime, and a unit-assignment checklist; the server assigns the
 * hex id. The create request is `{ displayName, description, role, unitIds }`
 * where `unitIds` are the assigned units' hex ids (the form resolves the
 * checked units to ids internally — call sites check the box by the unit's
 * display name).
 */

export interface AgentCreateOptions {
  /** Display name (the agent's human-friendly label). */
  displayName: string;
  role?: string;
  /**
   * Display names of the units to assign at creation. The form lists every
   * unit with a checkbox labelled `Assign to <displayName>`; we check the
   * matching boxes. The first assigned unit drives the success redirect to
   * `/explorer/units/<hex>?tab=Members`.
   */
  unitDisplayNames: string[];
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
  // The scratch form's first field is the (required) Display name.
  await page.getByLabel("Display name").waitFor();
}

/** Submit the form. Resolves when the page navigates away from `/agents/create`. */
export async function createAgent(page: Page, opts: AgentCreateOptions): Promise<void> {
  await openScratchAgentCreate(page);

  await page.getByLabel("Display name").fill(opts.displayName);
  if (opts.role) {
    await page.getByLabel("Role").fill(opts.role);
  }

  if (opts.runtime) {
    await page
      .getByTestId("agent-create-runtime-select")
      .selectOption(opts.runtime);
  }

  if (opts.model) {
    // Optional — the dropdown can be empty depending on the runtime catalog.
    const modelSelect = page.getByTestId("agent-create-model-select");
    const values = await modelSelect.evaluate((el) =>
      Array.from((el as HTMLSelectElement).options).map((o) => o.value),
    );
    if (values.includes(opts.model)) {
      await modelSelect.selectOption(opts.model);
    }
  }

  for (const unit of opts.unitDisplayNames) {
    await page
      .getByRole("checkbox", { name: new RegExp(`assign to ${unit}`, "i") })
      .first()
      .check();
  }

  // Submit. Post-#1561 / #2270 / #2427 the page navigates to the unit's
  // Explorer Members tab on success (`/explorer/units/<hex>?tab=Members`),
  // or to `/explorer` when no unit was assigned. Wait for any navigation
  // away from `/agents/create`.
  await page.getByTestId("agent-create-submit").click();
  await page.waitForURL((url) => !url.pathname.endsWith("/agents/create"), {
    timeout: 60_000,
  });
}
