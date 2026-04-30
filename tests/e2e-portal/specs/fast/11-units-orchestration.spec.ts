import { apiGet, apiPost } from "../../fixtures/api.js";
import { unitName } from "../../fixtures/ids.js";
import { DEFAULT_MODEL, PROVIDER_ID, TOOL_ID } from "../../fixtures/runtime.js";
import { expect, test } from "../../fixtures/test.js";

interface OrchestrationResponse {
  strategy?: string | null;
}

test.describe("units — orchestration tab", () => {
  test("strategy dropdown round-trips through the API", async ({
    page,
    tracker,
  }) => {
    const name = tracker.unit(unitName("orch"));
    await apiPost("/api/v1/tenant/units", {
      name,
      displayName: name,
      description: "Orchestration spec (e2e-portal)",
      tool: TOOL_ID,
      provider: PROVIDER_ID,
      model: DEFAULT_MODEL,
      hosting: "ephemeral",
      isTopLevel: true,
    });

    await page.goto(`/units/${name}`);
    await page.getByRole("tab", { name: /^orchestration$/i }).click();
    await expect(page.getByTestId("orchestration-tab")).toBeVisible();
    await expect(page.getByTestId("orchestration-strategy-card")).toBeVisible();

    const select = page.getByTestId("orchestration-strategy-select");
    const values = await select.evaluate((el) =>
      Array.from((el as HTMLSelectElement).options).map((o) => o.value),
    );
    // Pick the first non-empty value.
    const target = values.find((v) => v && v !== "default") ?? values[0]!;
    await select.selectOption(target);
    // Save button — accessible name varies; match generously.
    await page.getByRole("button", { name: /^save|apply$/i }).first().click();

    await expect
      .poll(
        async () => {
          const orch = await apiGet<OrchestrationResponse>(
            `/api/v1/tenant/units/${encodeURIComponent(name)}/orchestration`,
          );
          return orch.strategy ?? "";
        },
        { timeout: 10_000 },
      )
      .toBe(target);
  });
});
