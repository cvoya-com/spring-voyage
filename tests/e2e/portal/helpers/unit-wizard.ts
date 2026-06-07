import type { Page } from "@playwright/test";

import { expect } from "@playwright/test";

import { resolveUnitIdByDisplayName } from "../fixtures/api.js";
import { toExplorerHex } from "./nav.js";
import { AGENT_ID, DEFAULT_MODEL, PROVIDER_ID } from "../fixtures/runtime.js";

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
 * runtime=dapr-agent, provider=ollama (see fixtures/runtime.ts for the
 * rationale) and creates a top-level unit. Specs that diverge call it
 * with overrides or drive the wizard manually.
 *
 * Identity model (#2473): the operator-supplied `name` becomes the unit's
 * displayName; the server assigns the canonical hex id. The wizard
 * redirects to `/explorer/units/<hex>` on success — this helper resolves
 * that hex via the API after install and returns it so the caller can
 * navigate and register it for cleanup.
 */

export interface ScratchUnitOptions {
  /** Display-name slug. Becomes the unit's displayName; server assigns the hex. */
  name: string;
  displayName?: string;
  description?: string;
  /** Override the pinned model. Falls back to DEFAULT_MODEL. */
  model?: string;
  /** Per-step error tolerance — surfaces the wizard's stepError text on failure. */
  stepErrorTimeoutMs?: number;
}

export const WIZARD_DEFAULT_TIMEOUTS = {
  validationPanelMs: 90_000,
};

export interface ScratchUnitResult {
  /** URL the caller landed on (the `/explorer/units/<hex>` deep-link). */
  unitUrl: string;
  /** Server-assigned 32-char hex id of the created unit. */
  hex: string;
  /** Server-assigned dashed UUID of the created unit. */
  id: string;
  /** The display-name slug the unit was created with. */
  displayName: string;
}

/**
 * Create a top-level unit from scratch via the wizard. Resolves after the
 * wizard navigates away from `/units/create` (unit POSTed) and the helper
 * has parked the caller on the unit's Explorer detail page.
 *
 * The caller is responsible for tracking the unit's display name in the
 * artifact tracker for cleanup.
 */
export async function createScratchUnit(
  page: Page,
  opts: ScratchUnitOptions,
): Promise<ScratchUnitResult> {
  const displayName = opts.displayName ?? opts.name;
  const description = opts.description ?? `Created by e2e-portal: ${opts.name}`;

  await page.goto("/units/create");

  // ── Step 1 — Source ───────────────────────────────────────────────────
  // ADR-0035 / #1563: pick a source branch first. Scratch is the
  // closest analogue of the pre-#1563 "scratch mode".
  await page.getByTestId("source-card-scratch").click();
  await clickNext(page);

  // ── Step 2 — Identity ──────────────────────────────────────────────────
  // The Name / Display name inputs are wrapped in <label> spans (no
  // htmlFor binding), so target them by their wrapping-label accessible
  // name. `opts.name` lands in displayName server-side; the wizard's
  // "Name" field is the slug used for the address.
  await page.getByRole("textbox", { name: /^name/i }).first().fill(opts.name);
  await page
    .getByRole("textbox", { name: /display name/i })
    .first()
    .fill(displayName);
  const descField = page.getByRole("textbox", { name: /description/i }).first();
  if (await descField.isVisible().catch(() => false)) {
    await descField.fill(description);
  }
  // Top-level vs has-parents (#814). Click the explicit top-level chip.
  await page.getByTestId("parent-choice-top-level").click();

  await clickNext(page);

  // ── Step 3 — Execution ─────────────────────────────────────────────────
  // ADR-0038 / wizard reskin: the runtime dropdown is now labelled
  // "Agent Runtime" (testid `unit-create-runtime-select`); the provider
  // dropdown is "Model provider" (`unit-create-model-provider-select`);
  // the model dropdown is "Model" (`unit-create-model-select`). Prefer
  // the testids — they're stable across label copy changes.
  await page.getByTestId("unit-create-runtime-select").selectOption(AGENT_ID);
  // Provider dropdown only renders when the runtime is multi-provider.
  await page
    .getByTestId("unit-create-model-provider-select")
    .selectOption(PROVIDER_ID);
  // The model dropdown is hidden until the runtime catalog resolves.
  const modelSelect = page.getByTestId("unit-create-model-select");
  await modelSelect.waitFor({ state: "visible", timeout: 30_000 });
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
  // and the button is `install-unit-button`. The scratch branch POSTs the
  // unit synchronously, then auto-starts and POLLS the new unit until it
  // reaches a terminal lifecycle state before redirecting to
  // `/explorer/units/<hex>`. That validation (image pull, actor warm-up)
  // is slow / may never terminate on a cold credential-free stack, so we
  // do NOT wait on the wizard's own redirect. Instead we poll the API for
  // the freshly-created unit (the POST is synchronous) and navigate to its
  // Explorer detail ourselves — deterministic regardless of lifecycle.
  await page.getByTestId("install-unit-button").click();

  await expect
    .poll(async () => resolveUnitIdByDisplayName(displayName), {
      timeout: WIZARD_DEFAULT_TIMEOUTS.validationPanelMs,
      intervals: [500, 1000, 2000],
    })
    .not.toBeNull();
  const id = await resolveUnitIdByDisplayName(displayName);
  if (!id) {
    throw new Error(
      `Wizard install completed but no unit with displayName '${displayName}' was found.`,
    );
  }
  const hex = toExplorerHex(id);
  const target = `/explorer/units/${encodeURIComponent(hex)}?tab=Overview`;
  await page.goto(target);
  return { unitUrl: page.url(), hex, id, displayName };
}

async function clickNext(page: Page): Promise<void> {
  await page.getByRole("button", { name: /^next$/i }).click();
}
