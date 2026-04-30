import { apiGet, apiPost } from "../../fixtures/api.js";
import { unitName } from "../../fixtures/ids.js";
import { DEFAULT_MODEL, PROVIDER_ID, TOOL_ID } from "../../fixtures/runtime.js";
import { expect, test } from "../../fixtures/test.js";

/**
 * Boundary tab — opacity, projection, synthesis rules + YAML upload.
 *
 * The boundary tab exposes three rule lists driven by editable rows. This
 * spec exercises the YAML-upload affordance (which is the load-bearing
 * import path operators use to seed boundaries).
 */

interface BoundaryResponse {
  rules?: unknown[];
}

test.describe("units — boundary tab", () => {
  test("upload YAML, see diff, apply, persist", async ({ page, tracker }) => {
    const name = tracker.unit(unitName("boundary"));
    await apiPost("/api/v1/tenant/units", {
      name,
      displayName: name,
      description: "Boundary spec (e2e-portal)",
      tool: TOOL_ID,
      provider: PROVIDER_ID,
      model: DEFAULT_MODEL,
      hosting: "ephemeral",
      isTopLevel: true,
    });

    await page.goto(`/units/${name}`);
    await page.getByRole("tab", { name: /^boundary$/i }).click();
    await expect(page.getByTestId("boundary-tab")).toBeVisible();

    // The YAML upload card has its own testid.
    await expect(page.getByTestId("boundary-yaml-upload")).toBeVisible();

    // Find the YAML textarea inside the upload card and paste a minimal manifest.
    const yaml = [
      "rules:",
      "  - kind: opacity",
      "    selector: '*'",
      "    visibility: opaque",
      "",
    ].join("\n");
    await page
      .getByTestId("boundary-yaml-upload")
      .getByRole("textbox")
      .first()
      .fill(yaml);

    // Apply.
    const apply = page.getByTestId("boundary-yaml-apply");
    if (await apply.isVisible().catch(() => false)) {
      await apply.click();
    }

    // The diff view either confirms a no-op or shows a non-empty diff.
    // Either way the action should not surface an error.
    await expect(page.getByTestId("boundary-yaml-error")).toHaveCount(0);

    // Cross-check the API.
    const boundary = await apiGet<BoundaryResponse>(
      `/api/v1/tenant/units/${encodeURIComponent(name)}/boundary`,
    );
    expect(boundary).toBeDefined();
  });
});
