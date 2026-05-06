import { apiGet, apiPost } from "../../fixtures/api.js";
import { unitName } from "../../fixtures/ids.js";
import { AGENT_ID, DEFAULT_MODEL, PROVIDER_ID } from "../../fixtures/runtime.js";
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
      agent: AGENT_ID,
      provider: PROVIDER_ID,
      model: DEFAULT_MODEL,
      hosting: "ephemeral",
      isTopLevel: true,
    });

    await page.goto(
      `/units?node=${encodeURIComponent(name)}&tab=Orchestration`,
    );
    await expect(page.getByTestId("orchestration-tab")).toBeVisible();
    await expect(page.getByTestId("orchestration-strategy-card")).toBeVisible();

    const select = page.getByTestId("orchestration-strategy-select");
    const values = await select.evaluate((el) =>
      Array.from((el as HTMLSelectElement).options)
        .map((o) => o.value)
        // The first option is `MANIFEST_UNSET_VALUE` ("— inferred /
        // default —"); skip it so we actually drive a strategy change.
        .filter((v) => v && v !== "" && !v.startsWith("__unset")),
    );
    expect(values.length, "strategy select must offer at least one option").toBeGreaterThan(0);
    const target = values[0]!;
    // The strategy persists on change (no Save button — see
    // `setStrategyMutation` in orchestration-tab.tsx).
    await select.selectOption(target);

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
