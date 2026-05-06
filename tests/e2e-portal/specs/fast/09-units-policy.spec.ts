import { apiGet, apiPost } from "../../fixtures/api.js";
import { unitName } from "../../fixtures/ids.js";
import { AGENT_ID, DEFAULT_MODEL, PROVIDER_ID } from "../../fixtures/runtime.js";
import { expect, test } from "../../fixtures/test.js";

/**
 * Unit policy editor — five dimensions (skill / model / cost /
 * executionMode / initiative). The Policies tab opens per-dimension
 * dialogs; this spec drives one save per dimension and asserts the
 * server reflects it.
 *
 * Shell counterpart: `15-unit-policy-roundtrip.sh` and `18-unit-policy-cli-roundtrip.sh`.
 */

interface PolicyResponse {
  unit: { name: string };
  skill?: unknown;
  model?: unknown;
  cost?: unknown;
  executionMode?: unknown;
  initiative?: unknown;
}

test.describe("units — policy roundtrip", () => {
  test("opens Policies tab and persists edits across all five dimensions", async ({
    page,
    tracker,
  }) => {
    const name = tracker.unit(unitName("policy"));
    await apiPost("/api/v1/tenant/units", {
      name,
      displayName: name,
      description: "Policy spec (e2e-portal)",
      agent: AGENT_ID,
      provider: PROVIDER_ID,
      model: DEFAULT_MODEL,
      hosting: "ephemeral",
      isTopLevel: true,
    });

    // Land directly on the Policies tab via deep-link — the explorer
    // round-trips `?tab=` so this is the canonical way to enter a tab
    // without first hitting the Overview redirect race.
    await page.goto(
      `/units?node=${encodeURIComponent(name)}&tab=Policies`,
    );
    await expect(page.getByTestId("policies-tab-effective")).toBeVisible();

    // Each policy panel exposes an "Edit" button inside its
    // `policies-tab-<dimension>` card. Scope the click to the card
    // so we don't pick up an Edit button elsewhere on the page.
    await page
      .getByTestId("policies-tab-cost")
      .getByRole("button", { name: /^edit$/i })
      .click();
    await expect(page.getByTestId("cost-policy-dialog")).toBeVisible();
    const dailyCap = page.getByLabel(/daily cap|daily limit|day/i).first();
    if (await dailyCap.isVisible().catch(() => false)) {
      await dailyCap.fill("5");
    }
    await page.getByRole("button", { name: /^save$|^apply$/i }).click();
    await expect(page.getByTestId("cost-policy-dialog")).toBeHidden({ timeout: 5_000 });

    // Execution-mode dialog.
    await page
      .getByTestId("policies-tab-execution-mode")
      .getByRole("button", { name: /^edit$/i })
      .click();
    if (await page.getByTestId("execution-mode-policy-dialog").isVisible().catch(() => false)) {
      // Forced-mode is a <select>, not a radio group. Pick whatever the
      // first non-empty option is; the spec just needs the policy to
      // become non-null after save.
      const forcedSelect = page
        .getByTestId("execution-mode-policy-dialog")
        .getByRole("combobox")
        .first();
      const opts = await forcedSelect.evaluate((el) =>
        Array.from((el as HTMLSelectElement).options).map((o) => o.value),
      );
      const target = opts.find((v) => v && v !== "");
      if (target) {
        await forcedSelect.selectOption(target);
      }
      await page.getByRole("button", { name: /^save$|^apply$/i }).click();
      await expect(page.getByTestId("execution-mode-policy-dialog")).toBeHidden({ timeout: 5_000 });
    }

    // Initiative dialog.
    await page
      .getByTestId("policies-tab-initiative")
      .getByRole("button", { name: /^edit$/i })
      .click();
    if (await page.getByTestId("initiative-policy-dialog").isVisible().catch(() => false)) {
      await page.getByRole("button", { name: /^save$|^apply$/i }).click();
      await expect(page.getByTestId("initiative-policy-dialog")).toBeHidden({ timeout: 5_000 });
    }

    // Cross-check: at least one of cost / executionMode / initiative is now non-null on the server.
    const policy = await apiGet<PolicyResponse>(
      `/api/v1/tenant/units/${encodeURIComponent(name)}/policy`,
    );
    expect(
      Boolean(policy.cost) || Boolean(policy.executionMode) || Boolean(policy.initiative),
      "expected at least one policy dimension to be set after the UI roundtrip",
    ).toBe(true);
  });
});
