import { apiGet, seedAgent, seedUnit } from "../../fixtures/api.js";
import { agentName, unitName } from "../../fixtures/ids.js";
import { expect, test } from "../../fixtures/test.js";
import { gotoExplorerUnit } from "../../helpers/nav.js";

/**
 * Agent expertise — declared domains.
 *
 * Expertise was folded into the agent's Config → General sub-tab (#2331);
 * the panel writes to /api/v1/tenant/agents/{id}/expertise.
 */

interface ExpertiseResponse {
  domains?: { id?: string; name?: string }[];
}

test.describe("agents — expertise editor", () => {
  test("add a domain, save, persists", async ({ page, tracker }) => {
    const unit = tracker.unit(unitName("exp-host"));
    const ada = tracker.agent(agentName("exp-ada"));

    const u = await seedUnit(unit, {
      description: "Expertise spec (e2e-portal)",
    });
    const a = await seedAgent(ada, {
      description: "Expertise spec (e2e-portal)",
      unitHexIds: [u.hex],
    });

    // Config → General hosts the metadata form + the folded-in expertise
    // editor.
    await gotoExplorerUnit(page, a.hex, { tab: "Config", subtab: "General" });

    // The expertise editor is the only block on the page with an "Add
    // domain" button; scope to that editor container (the editor root is a
    // `space-y-3` div that owns both the Add-domain button and its Save
    // button) so we don't collide with the General panel's own Save.
    const editor = page
      .locator("div.space-y-3")
      .filter({ has: page.getByRole("button", { name: /^Add domain$/i }) })
      .first();
    await editor.getByRole("button", { name: /^Add domain$/i }).click();
    await editor
      .getByRole("textbox", { name: /Domain name \(row 1\)/i })
      .fill("rust");
    await editor.getByRole("button", { name: /^Save$/i }).click();

    await expect
      .poll(
        async () => {
          const exp = await apiGet<ExpertiseResponse>(
            `/api/v1/tenant/agents/${encodeURIComponent(a.hex)}/expertise`,
          );
          return (exp.domains ?? []).map((d) => d.id ?? d.name ?? "").filter(Boolean);
        },
        { timeout: 10_000 },
      )
      .toEqual(expect.arrayContaining([expect.stringMatching(/rust/i)]));
  });
});
