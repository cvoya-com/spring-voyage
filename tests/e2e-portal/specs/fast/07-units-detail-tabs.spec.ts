import { apiPost } from "../../fixtures/api.js";
import { unitName } from "../../fixtures/ids.js";
import { DEFAULT_MODEL, PROVIDER_ID, TOOL_ID } from "../../fixtures/runtime.js";
import { expect, test } from "../../fixtures/test.js";

/**
 * Unit detail page — every tab renders without throwing.
 *
 * The detail page lazy-loads each tab's data; this spec proves none of
 * them blow up against a freshly-created unit (no agents, no secrets,
 * no orchestration overrides).
 */

const TAB_LABELS = [
  "Overview",
  "Agents",
  "Boundary",
  "Execution",
  "Orchestration",
  "Policies",
  "Secrets",
  "Memberships",
] as const;

test.describe("units — detail page tabs", () => {
  test("every primary tab renders for a freshly-created unit", async ({
    page,
    tracker,
  }) => {
    const name = tracker.unit(unitName("tabs"));
    await apiPost("/api/v1/tenant/units", {
      name,
      displayName: name,
      description: "Detail tabs spec (e2e-portal)",
      tool: TOOL_ID,
      provider: PROVIDER_ID,
      model: DEFAULT_MODEL,
      hosting: "ephemeral",
      isTopLevel: true,
    });

    await page.goto(`/units/${name}`);
    await expect(page.getByRole("heading", { name })).toBeVisible();

    for (const label of TAB_LABELS) {
      const tab = page.getByRole("tab", { name: new RegExp(`^${label}$`, "i") });
      // Some tabs may be hidden on smaller layouts; fall back to a button match.
      const target = (await tab.isVisible().catch(() => false))
        ? tab
        : page.getByRole("button", { name: new RegExp(`^${label}$`, "i") });
      await target.first().click();
      // Tab panel for the corresponding panel — match by data-testid that the tab impl exposes.
      const panelTestIds: Record<(typeof TAB_LABELS)[number], string | null> = {
        Overview: null,
        Agents: null,
        Boundary: "boundary-tab",
        Execution: "execution-tab",
        Orchestration: "orchestration-tab",
        Policies: "policies-tab-effective",
        Secrets: null,
        Memberships: null,
      };
      const tid = panelTestIds[label];
      if (tid) {
        await expect(page.getByTestId(tid)).toBeVisible({ timeout: 10_000 });
      } else {
        // For tabs without a panel-level testid, just assert no error message
        // appears within the page after switching.
        await expect(page.getByRole("alert").filter({ hasText: /failed|error/i })).toHaveCount(0, { timeout: 5_000 });
      }
    }
  });
});
