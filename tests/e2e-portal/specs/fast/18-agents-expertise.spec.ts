import { apiGet, apiPost } from "../../fixtures/api.js";
import { agentName, unitName } from "../../fixtures/ids.js";
import { DEFAULT_MODEL, PROVIDER_ID, TOOL_ID } from "../../fixtures/runtime.js";
import { expect, test } from "../../fixtures/test.js";

/**
 * Agent expertise — declared domains.
 *
 * The agent detail page exposes an Expertise editor that PUTs to
 * /api/v1/tenant/agents/{id}/expertise.
 */

interface ExpertiseResponse {
  domains?: { id?: string; name?: string }[];
}

test.describe("agents — expertise editor", () => {
  test("add a domain, save, persists", async ({ page, tracker }) => {
    const unit = tracker.unit(unitName("exp-host"));
    const aId = tracker.agent(agentName("exp-ada"));

    await apiPost("/api/v1/tenant/units", {
      name: unit,
      displayName: unit,
      description: "Expertise spec (e2e-portal)",
      tool: TOOL_ID,
      provider: PROVIDER_ID,
      model: DEFAULT_MODEL,
      hosting: "ephemeral",
      isTopLevel: true,
    });
    await apiPost("/api/v1/tenant/agents", {
      id: aId,
      displayName: aId,
      unitIds: [unit],
    });

    await page.goto(`/agents/${aId}`);
    // Click into an "Expertise" tab if present, otherwise scroll/find the editor.
    const tab = page.getByRole("tab", { name: /^expertise$/i });
    if (await tab.first().isVisible().catch(() => false)) {
      await tab.first().click();
    }

    // Add a domain via the editor's text input + add button.
    const input = page.getByRole("textbox", { name: /domain|expertise|topic/i }).first();
    await input.fill("rust");
    await page.getByRole("button", { name: /^(add|save)$/i }).first().click();

    await expect
      .poll(
        async () => {
          const exp = await apiGet<ExpertiseResponse>(
            `/api/v1/tenant/agents/${encodeURIComponent(aId)}/expertise`,
          );
          return (exp.domains ?? []).map((d) => d.id ?? d.name ?? "").filter(Boolean);
        },
        { timeout: 10_000 },
      )
      .toEqual(expect.arrayContaining([expect.stringMatching(/rust/i)]));
  });
});
