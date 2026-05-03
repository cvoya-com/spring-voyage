import type { Page } from "@playwright/test";

import { expect } from "@playwright/test";

import { DEFAULT_MODEL, PROVIDER_ID, TOOL_ID } from "../fixtures/runtime.js";

/**
 * Drives `/units/create` — the post-ADR-0035 wizard at
 * `src/Cvoya.Spring.Web/src/app/units/create/page.tsx`.
 *
 * Steps (per `stepLabel()` in the wizard source) — branch-specific:
 *   Source step is always step 1 (catalog / browse / scratch).
 *   scratch:  Source → Identity → Execution → Connector → Install (5)
 *   catalog:  Source → Package  → Connector → Install            (4)
 *   browse:   Source → Browse (stub, submit disabled)            (2)
 *
 * #1563 removed YAML mode entirely; Template mode was superseded by the
 * Catalog branch (`spring package install`). Helpers here drive the
 * scratch branch (the analogue of the old "Mode = scratch") and the
 * catalog branch (the analogue of the old "Mode = template").
 *
 * The helper is opinionated for the scratch path: it pins
 * tool=dapr-agent, provider=ollama (see fixtures/runtime.ts for the
 * rationale) and creates a top-level unit. Specs that diverge call it
 * with overrides or drive the wizard manually.
 */

export interface ScratchUnitOptions {
  name: string;
  displayName?: string;
  description?: string;
  /** Override the pinned model. Falls back to DEFAULT_MODEL. */
  model?: string;
  /**
   * Set true to await the wizard's auto-validation phase reaching a
   * terminal state and redirecting into the explorer. Defaults to
   * false because the wizard's auto-start path is currently broken
   * for credential-free runtimes (Ollama) — it POSTs `/start` from
   * Draft and the actor rejects with `Draft → Starting` per #939.
   * Specs that need the unit to exist verify via API; specs that
   * exercise the validation UI itself should opt in explicitly once
   * that bug is resolved.
   */
  awaitValidation?: boolean;
  /** Per-step error tolerance — surfaces the wizard's stepError text on failure. */
  stepErrorTimeoutMs?: number;
}

export const WIZARD_DEFAULT_TIMEOUTS = {
  validationPanelMs: 90_000,
};

/**
 * Create a top-level unit from scratch via the wizard. Returns when the
 * wizard navigates away from `/units/create` (i.e. unit POSTed and the
 * page transitioned to `/units/<name>`).
 *
 * The caller is responsible for tracking the returned name in the
 * artifact tracker for cleanup.
 */
export async function createScratchUnit(
  page: Page,
  opts: ScratchUnitOptions,
): Promise<{ unitUrl: string }> {
  const displayName = opts.displayName ?? opts.name;
  const description = opts.description ?? `Created by e2e-portal: ${opts.name}`;

  await page.goto("/units/create");

  // ── Step 1 — Source ───────────────────────────────────────────────────
  // ADR-0035 / #1563: pick a source branch first. Scratch is the
  // closest analogue of the pre-#1563 "scratch mode".
  await page.getByTestId("source-card-scratch").click();
  await clickNext(page);

  // ── Step 2 — Identity ──────────────────────────────────────────────────
  await page.getByLabel("Name").or(page.getByRole("textbox", { name: /^name$/i })).first().fill(opts.name);
  await page.getByLabel("Display name").or(page.getByRole("textbox", { name: /display name/i })).first().fill(displayName);
  // Description is optional — the textarea fallback is by aria-label.
  const descField = page.getByLabel("Description").first();
  if (await descField.isVisible().catch(() => false)) {
    await descField.fill(description);
  }
  // Top-level vs has-parents (#814). Click the explicit top-level chip.
  await page.getByTestId("parent-choice-top-level").click();

  await clickNext(page);

  // ── Step 3 — Execution ─────────────────────────────────────────────────
  await page.getByLabel("Execution tool").selectOption(TOOL_ID);
  // Provider dropdown only renders when tool === dapr-agent.
  await page.getByLabel("LLM provider").selectOption(PROVIDER_ID);
  // The model dropdown is hidden until the runtime catalog resolves.
  // Wait for it to appear, then pick.
  const modelSelect = page.getByLabel("Model");
  await modelSelect.waitFor({ state: "visible", timeout: 30_000 });
  // Picking by exact value lets us target whichever Ollama model the
  // operator has installed (the catalog is the union of seed models +
  // tenant overrides, so this avoids hard-coding a label).
  const desired = opts.model ?? DEFAULT_MODEL;
  const optionValues = await modelSelect.evaluate((el) =>
    Array.from((el as HTMLSelectElement).options).map((o) => o.value),
  );
  if (optionValues.includes(desired)) {
    await modelSelect.selectOption(desired);
  } else if (optionValues.length > 0) {
    // Fall back to whatever the runtime offered first — the spec just
    // needs a valid choice to proceed past the wizard's "model required"
    // gate.
    const firstValue = optionValues[0]!;
    await modelSelect.selectOption(firstValue);
  } else {
    throw new Error(
      `Wizard model dropdown is empty — Ollama returned no models. Pull one ` +
        `via 'ollama pull ${desired}' before running this spec.`,
    );
  }

  await clickNext(page);

  // ── Step 4 — Connector ────────────────────────────────────────────────
  // Skip — explicit "Skip connector" affordance, falls back to Next when absent.
  const skipConnector = page.getByRole("button", { name: /skip connector|no connector|don.?t bind/i }).first();
  if (await skipConnector.isVisible().catch(() => false)) {
    await skipConnector.click();
  } else {
    await clickNext(page);
  }

  // ── Step 5 — Install ──────────────────────────────────────────────────
  // The pre-#1563 Finalize/Secrets pair is gone; Install is the final step
  // and the button is `install-unit-button`. After install completes the
  // wizard redirects to `/units` (or to the explorer deep-link once the
  // unit reaches a terminal state via the createdUnit polling effect).
  await page.getByTestId("install-unit-button").click();

  // Wait for the wizard to navigate away from `/units/create`. The
  // `installActive` effect on the page redirects on success; a transient
  // `install-status-failed` alert can flash mid-staging on the way to
  // active so we use the URL change as the authoritative signal and
  // only inspect the failed panel as a diagnostic when the URL never
  // moves within the deadline.
  try {
    await page.waitForURL((url) => !url.pathname.endsWith("/units/create"), {
      timeout: WIZARD_DEFAULT_TIMEOUTS.validationPanelMs,
    });
  } catch (err) {
    const failed = page.getByTestId("install-status-failed");
    if (await failed.isVisible().catch(() => false)) {
      const errText = (await failed.innerText().catch(() => "")) || "(no error text)";
      throw new Error(`Wizard install failed: ${errText}`);
    }
    throw err;
  }

  if (opts.awaitValidation) {
    // The post-install effect navigates to the explorer's deep-link
    // form once createdUnit reaches a terminal state. Wait for it.
    await page.waitForURL(/\/units\?[^#]*\bnode=[^&]+/, {
      timeout: WIZARD_DEFAULT_TIMEOUTS.validationPanelMs,
    });
    return { unitUrl: page.url() };
  }

  // The wizard's auto-redirect lands on /units (the explorer root) and
  // then transitions to the deep-link form once the unit is fully
  // validated. For specs that don't need to wait for terminal state we
  // navigate to the deep-link ourselves so the caller always lands on
  // the unit detail page.
  const target = `/units?node=${encodeURIComponent(opts.name)}&tab=Overview`;
  await page.goto(target);
  return { unitUrl: page.url() };
}

async function clickNext(page: Page): Promise<void> {
  await page.getByRole("button", { name: /^next$/i }).click();
}

/**
 * Wizard mode-card selector. The mode cards are buttons whose accessible
 * name starts with the title ("Scratch" / "Template" / "YAML") and is
 * followed by the descriptive blurb. Match on the title prefix so the
 * regex pins exactly one card per mode.
 */
export async function pickWizardMode(
  page: Page,
  mode: "scratch" | "template" | "yaml",
): Promise<void> {
  const titlePattern: Record<typeof mode, RegExp> = {
    scratch: /^Scratch\b/i,
    template: /^Template\b/i,
    yaml: /^YAML\b/i,
  };
  await page
    .getByRole("button", { name: titlePattern[mode] })
    .first()
    .click();
}
