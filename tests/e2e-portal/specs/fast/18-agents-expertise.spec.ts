import { apiGet, apiPost } from "../../fixtures/api.js";
import { agentName, unitName } from "../../fixtures/ids.js";
import { AGENT_ID, DEFAULT_MODEL, PROVIDER_ID } from "../../fixtures/runtime.js";
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
      agent: AGENT_ID,
      provider: PROVIDER_ID,
      model: DEFAULT_MODEL,
      hosting: "ephemeral",
      isTopLevel: true,
    });
    await apiPost("/api/v1/tenant/agents", {
      name: aId,
      displayName: aId,
      description: "Expertise spec (e2e-portal)",
      unitIds: [unit],
    });

    // Agent Config tab stacks Execution + Budget + Expertise panels.
    await page.goto(
      `/units?node=${encodeURIComponent(aId)}&tab=Config`,
    );

    // Editor starts empty — click "Add domain" to spawn a row, fill the
    // row's name field, then Save. The Config tab stacks Execution +
    // Budget + Expertise panels, each with its own Save button, so
    // scope to the Expertise section by its `aria-label`.
    const expertiseSection = page.getByLabel("Expertise", { exact: true });
    await expertiseSection.getByRole("button", { name: /^Add domain$/i }).click();
    await expertiseSection
      .getByRole("textbox", { name: /Domain name \(row 1\)/i })
      .fill("rust");
    await expertiseSection.getByRole("button", { name: /^Save$/i }).click();

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
